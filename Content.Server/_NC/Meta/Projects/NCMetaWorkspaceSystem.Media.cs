// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Interaction;

namespace Content.Server._NC.Meta.Projects;

[RegisterComponent]
public sealed partial class NCMetaRemovableMediaComponent : Component
{
    [DataField] public int CapacityBytes = 2 * 1024 * 1024;
}

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed record MediaPackage(string DeviceId, string Name, NCMetaProjectFile[] Files,
        NCMetaRelease[] Releases, long Revision);
    [Dependency] private SharedInteractionSystem _mediaInteraction = default!;

    private async Task<NCMetaMutationOutcome> TransferMediaAsync(Context context, NCMetaDeviceRecord device,
        NCMetaProject project, NCMetaWorkspaceRequestMessage request)
    {
        using var lease = _persistence.TryBeginNativeMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        var target = await OnMainAsync(() =>
        {
            if (!IsCurrent(context) || !_devices.TryResolve(request.DeviceId, out var media) ||
                !_devices.CanManage(media.Entity, context.User) ||
                !_mediaInteraction.InRangeUnobstructed(context.Terminal, media.Entity, range: 2) ||
                !TryComp<NCMetaRemovableMediaComponent>(media.Entity, out var component)) return null;
            return new { media.Id, media.Entity, media.DeviceEpoch, media.AuthorityEpoch, component.CapacityBytes };
        });
        if (target == null || target.CapacityBytes is < 1 or > 16 * 1024 * 1024) return new(false, "nc-meta-error-access");
        var authority = await CaptureInstallationAuthorityAsync(context, target.Entity);
        if (authority == null) return new(false, "nc-meta-error-access");
        bool Current() => IsCurrent(context) && _devices.TryGetEntry(target.Entity, out var current) &&
            current.Id == target.Id && current.DeviceEpoch == target.DeviceEpoch && current.AuthorityEpoch == target.AuthorityEpoch &&
            _devices.CanManage(target.Entity, context.User) &&
            _mediaInteraction.InRangeUnobstructed(context.Terminal, target.Entity, range: 2);
        var stored = await _persistence.ReadSnapshotAsync("meta.media", target.Id);
        if (request.Operation == NCMetaWorkspaceOperation.ExportMedia)
        {
            var releases = project.Releases.TakeLast(1).ToArray();
            var references = project.Files.Select(file => (file.Digest, file.Bytes))
                .Concat(releases.SelectMany(release => (release.Resources ?? []).Concat(release.Sources ?? []))
                    .Select(file => (file.Digest, file.Bytes))).ToArray();
            var bytes = references.DistinctBy(file => file.Digest).Sum(file => (long) file.Bytes);
            foreach (var release in releases)
            {
                var artifact = await _persistence.ReadArtifactAsync(release.Digest);
                if (artifact == null) return new(false, "nc-meta-error-artifact");
                bytes += artifact.Length;
            }
            if (bytes > target.CapacityBytes) return new(false, "nc-meta-error-quota");
            var package = new MediaPackage(target.Id, project.Name, project.Files, releases, checked((stored?.AggregateVersion ?? 0) + 1));
            var payload = JsonSerializer.Serialize(package);
            if (payload.Length > 256 * 1024 || !await OnMainAsync(Current)) return new(false, "nc-meta-error-access");
            var result = await _contactDatabase.ApplyNCPersistentMutationAsync(new(request.RequestId, "meta.media.export", "meta.owner", context.Owner,
                Guid.NewGuid(), "meta.media.exported", "meta.media", target.Id, stored?.AggregateVersion ?? 0, 1, payload, "{}", "{}",
                request.RequestId, null, context.Epoch, context.Time, [], RequestFingerprint: NCMetaPersistenceSystem.Fingerprint(
                    context.Owner, target.Id, project.Id.ToString("D"), project.Revision.ToString()), Authority: authority));
            var applied = result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
            return new(applied, applied ? "nc-meta-media-exported" : "nc-meta-error-revision");
        }
        if (stored == null || stored.Payload.Length > 256 * 1024) return new(false, "nc-meta-error-storage");
        MediaPackage? imported;
        try { imported = JsonSerializer.Deserialize<MediaPackage>(stored.Payload, new JsonSerializerOptions { MaxDepth = 16 }); }
        catch (JsonException) { return new(false, "nc-meta-error-artifact"); }
        if (imported is not { Files.Length: > 0 and <= 64, Releases.Length: <= 1 } || imported.DeviceId != target.Id ||
            imported.Revision != stored.AggregateVersion || imported.Files.Any(file => file == null || !MetaPaths.IsValid(file.Path) ||
                file.Bytes is < 0 or > 256 * 1024 || file.Digest.Length != 64) ||
            imported.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != imported.Files.Length)
            return new(false, "nc-meta-error-artifact");
        var sourceProject = new NCMetaProject(Guid.Empty, context.Owner, imported.Name, 1, imported.Files, []);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in imported.Files)
        {
            var source = await _persistence.ReadSourceAsync(sourceProject, file.Id);
            if (source == null) return new(false, "nc-meta-error-artifact");
            files.Add(file.Path, source);
        }
        foreach (var release in imported.Releases)
            if (await ReadVerifiedAsync(release) == null) return new(false, "nc-meta-error-artifact");
        if (!await OnMainAsync(Current)) return new(false, "nc-meta-error-access");
        return await _persistence.CloneProjectAsync(request.RequestId, device, imported.Name, files,
            context.Epoch, context.Time, imported.Releases, authority);
    }
}
