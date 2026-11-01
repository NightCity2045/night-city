// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Durable authoring service. All ECS/clock context is captured before awaiting database work.</summary>
public sealed partial class NCMetaPersistenceSystem : EntitySystem
{
    public const string ProjectType = "meta.project";
    public const string InstallationType = "meta.installation";
    private const int MaximumProjectBytes = 1024 * 1024;
    private const int MaximumFileBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMetaSnapshotSystem _snapshots = default!;

    public Task<NCAggregateSnapshotData?> ReadSnapshotAsync(string type, string id) =>
        type.StartsWith("meta.", StringComparison.Ordinal) ? _snapshots.ReadCurrentAsync(type, id) : Task.FromResult<NCAggregateSnapshotData?>(null);

    public bool TryCaptureTime(out NCSessionEpoch epoch, out NCActiveWorldTime time)
    {
        epoch = default; time = default;
        return IsAcceptingCommands && _clock.TryGetCurrentTime(out epoch, out time);
    }

    public async Task<NCMetaProject?> ReadProjectAsync(Guid projectId, string owner)
    {
        var snapshot = await _snapshots.ReadCurrentAsync(ProjectType, projectId.ToString("D"));
        if (snapshot == null || snapshot.Payload.Length > 2 * MaximumProjectBytes || await IsProjectDeletedAsync(projectId))
            return null;
        try
        {
            var project = JsonSerializer.Deserialize<NCMetaProject>(snapshot.Payload, Json);
            return project != null && project.Id == projectId && project.Owner == owner &&
                   project.Revision == snapshot.AggregateVersion && ValidateProject(project) ? project : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<string?> ReadSourceAsync(NCMetaProject project, Guid fileId)
    {
        if (await IsProjectDeletedAsync(project.Id)) return null;
        var file = project.Files.FirstOrDefault(value => value.Id == fileId);
        if (file == null || file.Bytes is < 0 or > MaximumFileBytes)
            return null;
        var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
        if (bytes == null || bytes.Length != file.Bytes || await IsProjectDeletedAsync(project.Id))
            return null;
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    public async Task<NCMetaMutationOutcome> CreateProjectAsync(Guid requestId, Guid projectId, string owner,
        string name, NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (projectId == Guid.Empty || owner.Length is < 1 or > 128 || name.Length is < 1 or > 64)
            return new(false, "nc-meta-error-input");
        var source = Utf8.GetBytes("module program.main;\nimport meta.system;\n\nfn main() -> void {\n    system.log(\"META\");\n}\n");
        var digest = await _database.StoreNCMetaBlobAsync(source);
        var project = new NCMetaProject(projectId, owner, name, 1,
            [new NCMetaProjectFile(Guid.NewGuid(), "src/main.meta", 1, digest, source.Length)], []);
        var fingerprint = Fingerprint(owner, projectId.ToString("D"), name);
        return await WriteProjectAsync(requestId, project, 0, fingerprint, epoch, time, authority);
    }

    public async Task<NCMetaMutationOutcome> SaveSourceAsync(Guid requestId, Guid projectId, Guid fileId,
        string owner, long baseRevision, string path, string source, NCSessionEpoch epoch, NCActiveWorldTime time,
        NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (requestId == Guid.Empty || projectId == Guid.Empty || fileId == Guid.Empty ||
            !MetaPaths.IsValid(path) || source.Length > MaximumFileBytes)
            return new(false, "nc-meta-error-input");
        byte[] bytes;
        try
        {
            if (source.StartsWith('\ufeff')) source = source[1..];
            source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (source.Contains('\0'))
                return new(false, "nc-meta-error-input");
            bytes = Utf8.GetBytes(source);
        }
        catch (EncoderFallbackException) { return new(false, "nc-meta-error-input"); }
        if (bytes.Length > MaximumFileBytes)
            return new(false, "nc-meta-error-quota");
        var fingerprint = Fingerprint(owner, projectId.ToString("D"), fileId.ToString("D"),
            baseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), path, source);
        var replay = await ReadReplayAsync(requestId, owner, fingerprint);
        if (replay != null)
            return replay;
        var project = await ReadProjectAsync(projectId, owner);
        if (project == null)
            return new(false, "nc-meta-error-access");
        var previous = project.Files.FirstOrDefault(value => value.Id == fileId);
        if (previous == null ? baseRevision != 0 : previous.Revision != baseRevision)
            return new(false, "nc-meta-error-revision", previous?.Revision ?? 0);
        if (project.Files.Any(value => value.Id != fileId && value.Path == path))
            return new(false, "nc-meta-error-path");
        if (project.Files.Length >= 64 && previous == null ||
            project.Files.Sum(value => (long) value.Bytes) - (previous?.Bytes ?? 0) + bytes.Length > MaximumProjectBytes)
            return new(false, "nc-meta-error-quota");
        var digest = await _database.StoreNCMetaBlobAsync(bytes);
        var file = new NCMetaProjectFile(fileId, path, checked(baseRevision + 1), digest, bytes.Length);
        var updated = project with
        {
            Revision = checked(project.Revision + 1),
            Files = project.Files.Where(value => value.Id != fileId).Append(file).OrderBy(value => value.Path,
                StringComparer.Ordinal).ToArray(),
        };
        return await WriteProjectAsync(requestId, updated, project.Revision, fingerprint, epoch, time, authority);
    }

    public async Task<NCMetaMutationOutcome> PublishReleaseAsync(Guid requestId, NCMetaProject project,
        byte[] artifactBytes, string compilerBuild, NCSessionEpoch epoch, NCActiveWorldTime time,
        Content.Meta.Language.MetaExportCertificate? exports = null, NCMetaDependencyRef[]? dependencies = null,
        string buildDeviceId = "", NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (await IsProjectDeletedAsync(project.Id)) return new(false, "nc-meta-error-access");
        if (project.Releases.Length >= 128 || artifactBytes.Length > MaximumProjectBytes ||
            dependencies is { Length: > 16 } || dependencies?.Any(reference => !ValidDependency(reference)) == true ||
            dependencies is { Length: > 0 } && !Content.Meta.Commerce.MetaSoftwarePolicy.Device(buildDeviceId))
            return new(false, "nc-meta-error-quota");
        var digest = await _database.StoreNCMetaBlobAsync(artifactBytes);
        var release = new NCMetaRelease(requestId, digest, project.Revision, compilerBuild, exports,
            project.Files.Where(file => !file.Path.EndsWith(".meta", StringComparison.Ordinal))
                .Select(file => new NCMetaReleaseResource(file.Path, file.Digest, file.Bytes, file.Id)).ToArray(),
            project.Files.Where(file => file.Path.EndsWith(".meta", StringComparison.Ordinal))
                .Select(file => new NCMetaReleaseResource(file.Path, file.Digest, file.Bytes, file.Id)).ToArray(),
            dependencies?.ToArray(), BuildAuthorization: dependencies is { Length: > 0 }
                ? new(project.Owner, buildDeviceId, digest, Fingerprint(JsonSerializer.Serialize(dependencies, Json))) : null);
        var updated = project with
        {
            Revision = checked(project.Revision + 1),
            Releases = project.Releases.Append(release).ToArray(),
        };
        return await WriteProjectAsync(requestId, updated, project.Revision,
            Fingerprint(project.Owner, project.Id.ToString("D"), digest, JsonSerializer.Serialize(dependencies ?? [], Json)), epoch, time, authority);
    }

    private async Task<NCMetaMutationOutcome?> ReadReplayAsync(Guid requestId, string owner, string fingerprint,
        string commandType = "meta.project.write")
    {
        var replay = await _database.GetNCPersistentCommandResultAsync(requestId);
        if (replay == null)
            return null;
        if (replay.CommandType != commandType || replay.ScopeType != "meta.owner" ||
            replay.ScopeId != owner || replay.RequestFingerprint != fingerprint)
            return new(false, "nc-meta-error-request");
        return await CanReplayCommandAsync(replay, requestId, commandType, owner, fingerprint)
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(replay.ResultPayload!, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, "nc-meta-error-storage");
    }

    private async Task<NCMetaMutationOutcome> WriteProjectAsync(Guid requestId, NCMetaProject project,
        long expectedVersion, string fingerprint, NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        if (await IsProjectDeletedAsync(project.Id)) return new(false, "nc-meta-error-access");
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-saved", project.Revision);
        var payload = JsonSerializer.Serialize(project, Json);
        if (Utf8.GetByteCount(payload) > 2 * MaximumProjectBytes) return new(false, "nc-meta-error-quota");
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            requestId, "meta.project.write", "meta.owner", project.Owner, Guid.NewGuid(), "meta.project.saved",
            ProjectType, project.Id.ToString("D"), expectedVersion, 1, payload,
            JsonSerializer.Serialize(new { project.Id, project.Revision }, Json), JsonSerializer.Serialize(outcome, Json),
            requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload!, Json)!
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict
                ? "nc-meta-error-revision" : "nc-meta-error-storage");
    }

    private async Task<bool> IsProjectDeletedAsync(Guid projectId) =>
        await _snapshots.ReadCurrentAsync(ServerDbBase.NCMetaProjectTombstoneType, projectId.ToString("D")) != null;

    private static bool ValidateProject(NCMetaProject project) => project.Files is { Length: >= 1 and <= 64 } &&
        project.Releases is { Length: <= 128 } && project.Files.All(value => value is { Id: var id, Path: not null, Digest.Length: 64 } && id != Guid.Empty &&
            MetaPaths.IsValid(value.Path) && value.Revision > 0 && value.Bytes is >= 0 and <= MaximumFileBytes) &&
        project.Releases.All(ValidRelease) &&
        project.Files.Select(value => value.Id).Distinct().Count() == project.Files.Length &&
        project.Files.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() == project.Files.Length &&
        project.Files.Sum(value => (long) value.Bytes) <= MaximumProjectBytes;

    private static bool ValidDependency(NCMetaDependencyRef? reference) => reference is
        { ProductId: var productId, ReleaseId: var releaseId, PackageIdentity.Length: > 0 and <= 128, Version.Length: > 0 and <= 128 } &&
        productId != Guid.Empty && releaseId != Guid.Empty &&
        Content.Meta.Commerce.MetaSoftwarePolicy.Digest(reference.BlobDigest) && Content.Meta.Commerce.MetaSoftwarePolicy.Digest(reference.ArtifactDigest);

    private static bool ValidRelease(NCMetaRelease? release) => release is
        { Digest.Length: 64, CompilerBuild.Length: <= 128, SourceRevision: > 0 } &&
        Content.Meta.Commerce.MetaSoftwarePolicy.Digest(release.Digest) &&
        ValidResources(release.Resources) && ValidResources(release.Sources) &&
        (release.Dependencies == null || release.Dependencies.Length <= 16 && release.Dependencies.All(ValidDependency) &&
            release.Dependencies.Select(reference => reference.PackageIdentity).Distinct(StringComparer.Ordinal).Count() == release.Dependencies.Length) &&
        (release.CommercialOrigin == null || release.CommercialOrigin is { ProductId: var productId, Major: > 0, PublisherProjectId: var projectId, ReleaseId: var releaseId } &&
            productId != Guid.Empty && projectId != Guid.Empty && releaseId != Guid.Empty) &&
        (release.BuildAuthorization == null || release.BuildAuthorization is { Owner.Length: > 0 and <= 128 } build &&
            Content.Shared._NC.Persistence.Identity.NCPartyRef.TryParse(build.Owner, out _) &&
            Content.Meta.Commerce.MetaSoftwarePolicy.Device(build.DeviceId) && build.ArtifactDigest == release.Digest &&
            build.DependencyFingerprint == Fingerprint(JsonSerializer.Serialize(release.Dependencies ?? [], Json)));

    private static bool ValidResources(NCMetaReleaseResource[]? resources) => resources == null ||
        resources.Length <= 256 && resources.All(resource => resource is { Bytes: >= 0 and <= MaximumFileBytes } &&
            Content.Meta.Hosting.MetaPaths.IsValid(resource.Path) && Content.Meta.Commerce.MetaSoftwarePolicy.Digest(resource.Digest)) &&
        resources.Select(resource => resource.Path).Distinct(StringComparer.Ordinal).Count() == resources.Length &&
        resources.Sum(resource => (long) resource.Bytes) <= MaximumProjectBytes;

    internal static string Fingerprint(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(JsonSerializer.Serialize(parts))));
}
