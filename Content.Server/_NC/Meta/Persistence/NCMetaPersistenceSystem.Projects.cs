// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    public async Task<NCMetaMutationOutcome> ManageProjectAsync(NCMetaWorkspaceOperation operation, Guid requestId,
        NCMetaDeviceRecord device, NCMetaProject project, Guid fileId, long baseRevision, string argument,
        NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaAuthorityConstraints? authority = null)
    {
        using var lease = TryBeginMutation();
        if (lease == null) return new(false, "nc-meta-error-busy");
        if (project.Owner != device.Owner || !device.Projects.Contains(project.Id) || requestId == Guid.Empty)
            return new(false, "nc-meta-error-access");
        var writes = new List<NCPersistentAggregateWrite>();
        var fingerprint = Fingerprint(operation.ToString(), device.Owner, device.DeviceId, project.Id.ToString("D"),
            fileId.ToString("D"), baseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), argument);
        var existing = await _database.GetNCPersistentCommandResultAsync(requestId);
        if (existing != null)
            return await CanReplayCommandAsync(existing, requestId, "meta.project.manage", device.Owner, fingerprint)
                ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(existing.ResultPayload!, Json) ?? new(false, "nc-meta-error-storage")
                : new(false, "nc-meta-error-request");
        if (await IsProjectDeletedAsync(project.Id)) return new(false, "nc-meta-error-access");
        var updatedProject = project;
        var updatedDevice = device;
        switch (operation)
        {
            case NCMetaWorkspaceOperation.CreateProject:
                if (!ValidName(argument) || device.Projects.Length >= 64) return new(false, "nc-meta-error-quota");
                var id = StableGuid("new-project:" + requestId.ToString("D"));
                var source = Utf8.GetBytes("module program.main;\nimport meta.system;\nfn main() -> void { system.log(\"META\"); }\n");
                var digest = await _database.StoreNCMetaBlobAsync(source);
                updatedProject = new NCMetaProject(id, device.Owner, argument, 1,
                    [new NCMetaProjectFile(StableGuid("file:" + id), "src/main.meta", 1, digest, source.Length)], []);
                updatedDevice = device with { Projects = device.Projects.Append(id).ToArray(), ActiveProject = id, Revision = checked(device.Revision + 1) };
                writes.Add(ProjectWrite(updatedProject, 0));
                break;
            case NCMetaWorkspaceOperation.SelectProject:
                updatedDevice = device with { ActiveProject = project.Id, Revision = checked(device.Revision + 1) };
                break;
            case NCMetaWorkspaceOperation.RenameProject:
                if (!ValidName(argument)) return new(false, "nc-meta-error-input");
                if (baseRevision != project.Revision) return new(false, "nc-meta-error-revision");
                updatedProject = project with { Name = argument, Revision = checked(project.Revision + 1) };
                writes.Add(ProjectWrite(updatedProject, project.Revision));
                break;
            case NCMetaWorkspaceOperation.DeleteProject:
                if (device.Projects.Length <= 1 || baseRevision != project.Revision) return new(false, "nc-meta-error-revision");
                // The exact project revision and marker commit together. Its source is retained for
                // bounded recovery, then retired without touching installations or publications.
                updatedProject = project with { Revision = checked(project.Revision + 1) };
                writes.Add(ProjectWrite(updatedProject, project.Revision));
                updatedDevice = device with { Projects = device.Projects.Where(value => value != project.Id).ToArray(),
                    ActiveProject = device.Projects.First(value => value != project.Id), Revision = checked(device.Revision + 1) };
                writes.Add(new NCPersistentAggregateWrite(Guid.NewGuid(), "meta.project.deleted", ServerDbBase.NCMetaProjectTombstoneType,
                    project.Id.ToString("D"), 0, 1, JsonSerializer.Serialize(new NCMetaProjectTombstoneData(project.Id, project.Owner), Json), "{}"));
                break;
            case NCMetaWorkspaceOperation.RenameFile:
            case NCMetaWorkspaceOperation.DeleteFile:
                var file = project.Files.FirstOrDefault(value => value.Id == fileId);
                if (file == null || file.Revision != baseRevision) return new(false, "nc-meta-error-revision");
                if (operation == NCMetaWorkspaceOperation.RenameFile && (!MetaPaths.IsValid(argument) ||
                    project.Files.Any(value => value.Id != fileId && value.Path == argument))) return new(false, "nc-meta-error-path");
                if (operation == NCMetaWorkspaceOperation.DeleteFile && project.Files.Length <= 1) return new(false, "nc-meta-error-input");
                updatedProject = project with { Revision = checked(project.Revision + 1), Files = project.Files
                    .Where(value => value.Id != fileId).Concat(operation == NCMetaWorkspaceOperation.DeleteFile ? [] :
                        new[] { file with { Path = argument, Revision = checked(file.Revision + 1) } }).OrderBy(value => value.Path, StringComparer.Ordinal).ToArray() };
                writes.Add(ProjectWrite(updatedProject, project.Revision));
                break;
            default: return new(false, "nc-meta-error-input");
        }
        if (updatedDevice != device)
            writes.Add(new NCPersistentAggregateWrite(Guid.NewGuid(), "meta.device.projects-changed", "meta.device", device.DeviceId,
                device.Revision, 1, JsonSerializer.Serialize(updatedDevice, Json), "{}"));
        var outcome = new NCMetaMutationOutcome(true, "nc-meta-saved", updatedProject.Revision);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(requestId,
            "meta.project.manage", "meta.owner", device.Owner, writes.ToArray(), JsonSerializer.Serialize(outcome, Json),
            requestId, null, epoch, time, [], RequestFingerprint: fingerprint, Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted && result.ResultPayload != null
            ? JsonSerializer.Deserialize<NCMetaMutationOutcome>(result.ResultPayload, Json) ?? new(false, "nc-meta-error-storage")
            : new(false, result.Status == NCPersistentMutationStatus.VersionConflict ? "nc-meta-error-revision" : "nc-meta-error-storage");
    }

    private static bool ValidName(string name) => name.Length is > 0 and <= 64 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
    private static NCPersistentAggregateWrite ProjectWrite(NCMetaProject project, long expectedVersion) =>
        new(Guid.NewGuid(), "meta.project.changed", ProjectType, project.Id.ToString("D"), expectedVersion, 1,
            JsonSerializer.Serialize(project, Json), "{}");
}
