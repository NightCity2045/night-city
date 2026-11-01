// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    public async Task<NCMetaMutationOutcome> CloneProjectAsync(Guid requestId, NCMetaDeviceRecord device,
        string name, IReadOnlyDictionary<string, string> files, NCSessionEpoch epoch, NCActiveWorldTime time,
        NCMetaRelease[]? releases = null, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (requestId == Guid.Empty || !ValidName(name) || device.Projects.Length >= 64 || files.Count is < 1 or > 64 ||
            files.Any(file => !MetaPaths.IsValid(file.Key) || Utf8.GetByteCount(file.Value) > MaximumFileBytes) ||
            files.Sum(file => (long) Utf8.GetByteCount(file.Value)) > MaximumProjectBytes ||
            releases is { Length: > 128 } || releases?.Any(release => !ValidRelease(release)) == true)
            return new(false, "nc-meta-error-quota");
        var id = StableGuid("project-clone:" + device.Owner + ":" + requestId);
        var references = new List<NCMetaProjectFile>();
        foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            var bytes = Utf8.GetBytes(file.Value);
            var digest = await _database.StoreNCMetaBlobAsync(bytes);
            references.Add(new NCMetaProjectFile(StableGuid("file-clone:" + id + ":" + file.Key), file.Key, 1, digest, bytes.Length));
        }
        var project = new NCMetaProject(id, device.Owner, name, 1, references.ToArray(), releases ?? []);
        var updated = device with { Projects = device.Projects.Append(id).ToArray(), ActiveProject = id, Revision = checked(device.Revision + 1) };
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-saved", 1);
        var fingerprint = Fingerprint(device.Owner, device.DeviceId, name,
            JsonSerializer.Serialize(references.Select(file => new { file.Path, file.Digest }), Json), JsonSerializer.Serialize(releases ?? [], Json));
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(requestId,
            "meta.project.clone", "meta.owner", device.Owner,
            [ProjectWrite(project, 0), new NCPersistentAggregateWrite(Guid.NewGuid(), "meta.device.project-cloned", "meta.device",
                device.DeviceId, device.Revision, 1, JsonSerializer.Serialize(updated, Json), "{}")],
            JsonSerializer.Serialize(outcome, Json), requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted && result.ResultPayload != null
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict ? "nc-meta-error-revision" : "nc-meta-error-storage");
    }
}
