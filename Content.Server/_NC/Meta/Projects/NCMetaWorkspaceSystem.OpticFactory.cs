// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Bytecode;
using Content.Meta.Verification;
using Content.Server._NC.Medical.Cyberware.Optics;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    [Dependency] private IResourceManager _opticResources = default!;
    [Dependency] private NCCyberOpticsSystem _opticHardware = default!;
    private readonly HashSet<EntityUid> _opticCommissioning = [];

    /// <summary>Explicit native commissioning uses the ordinary durable compiler/install/grant path.</summary>
    public async Task<bool> TryCommissionOpticAsync(EntityUid board, EntityUid user)
    {
        if (_opticCommissioning.Count >= 16 || !_opticCommissioning.Add(board)) return false;
        try
        {
            if (await _authority.CaptureWorkspaceManagementAsync(board, user) == null) return false;
            var context = await OnMainAsync(() => HasComp<NCOpticControllerComponent>(board) &&
                Comp<NCOpticControllerComponent>(board).FactoryProgram && _opticHardware.TryGetServiceBench(board, out _) &&
                _hardware.TrySetMaintenance(board, user, true) ? CaptureWorkspaceContext(board, user) : null);
            if (context == null) return false;
            var installed = await _persistence.ReadInstallationsAsync(context.DeviceId, context.Owner);
            // Never replace custom or already commissioned firmware with a factory image.
            if (installed.Any(value => value.SlotKind == "Firmware")) return false;
            var source = await OnMainAsync(() => _opticResources.ContentFileReadAllText(new ResPath("/Meta/Firmware/optic-basic.meta")));
            var device = await _persistence.ReadDeviceAsync(context.DeviceId);
            if (device == null) return false;
            NCMetaProject? project = null;
            foreach (var id in device.Projects)
            {
                var candidate = await _persistence.ReadProjectAsync(id, context.Owner);
                if (candidate?.Name == "Optics Basic") { project = candidate; break; }
            }
            if (project == null)
            {
                var active = await _persistence.ReadProjectAsync(device.ActiveProject, context.Owner);
                if (active == null) return false;
                var request = Guid.NewGuid();
                var created = await _persistence.ManageProjectAsync(NCMetaWorkspaceOperation.CreateProject, request, device, active,
                    Guid.Empty, 0, "Optics Basic", context.Epoch, context.Time, WorkspaceAuthority(context));
                if (!created.Success) return false;
                project = await _persistence.ReadProjectAsync(NCMetaPersistenceSystem.StableGuid("new-project:" + request.ToString("D")), context.Owner);
                if (project == null) return false;
                var file = project.Files.Single();
                var saved = await _persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, file.Id, context.Owner, file.Revision,
                    file.Path, source, context.Epoch, context.Time, WorkspaceAuthority(context));
                if (!saved.Success) return false;
                project = await _persistence.ReadProjectAsync(project.Id, context.Owner);
            }
            if (project == null || project.Files.Length != 1 || await _persistence.ReadSourceAsync(project, project.Files[0].Id) != source)
                return false;
            var compilation = await Task.Run(() => new MetaCompiler().Compile(new([new("optic-basic.meta", source)], "optics.basic", "nc.optics.basic")));
            if (compilation.Artifact == null) return false;
            var artifact = MetaVerifier.Verify(compilation.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(compilation.Artifact)));
            if (!await OnMainAsync(() => IsCurrent(context))) return false;
            var published = await _persistence.PublishReleaseAsync(Guid.NewGuid(), project, compilation.Artifact, MetaCompiler.BuildId,
                context.Epoch, context.Time, compilation.Exports, [], context.DeviceId, WorkspaceAuthority(context));
            if (!published.Success) return false;
            project = await _persistence.ReadProjectAsync(project.Id, context.Owner);
            if (project == null) return false;
            var release = project.Releases.Last();
            var scopes = artifact.Manifest.RequiredScopes.ToArray();
            var bindings = new[] { new NCMetaBinding("eye", context.DeviceId, artifact.GetLinks().Single().ContractHash, scopes) };
            var stamp = await OnMainAsync(() => IsCurrent(context) && _hardware.TryCaptureFirmwareService(board, user, out var captured) ? captured : null);
            var authority = await CaptureInstallationAuthorityAsync(context, board);
            if (stamp == null || authority == null) return false;
            var outcome = await TryCommitInstallationSwapAsync(board, user, board, artifact, bindings, "Firmware", null,
                guard => _persistence.InstallAsync(Guid.NewGuid(), context.DeviceId, context.Owner, project, release, bindings, true,
                    context.Epoch, context.Time, 1048576, 1, "Firmware", guard, 131072, authority: authority),
                () => IsCurrent(context), stamp);
            if (!outcome.Success) return false;
            var installation = (await _persistence.ReadInstallationsAsync(context.DeviceId, context.Owner)).Single(value => value.SlotKind == "Firmware");
            var members = MetaApiCatalog.Default.Members.Where(member => member.Owner == "meta.implants.CyberEye" && scopes.Contains(member.Scope)).Select(member => member.QualifiedName).ToList();
            var policy = await _authority.ReadGrantPolicyAsync(board, user, context.DeviceId, installation.Id, artifact);
            var grants = await _authority.TryIssueInstallationGrantsAsync(board, user, board, Guid.NewGuid(), installation, artifact,
                new NCMetaGrantApprovalRequest(policy?.Revision ?? 0, true, true, null, [new("eye", context.DeviceId, members, scopes.ToList())]));
            if (!grants.Success) return false;
            return await OnMainAsync(() => IsCurrent(context) && _hardware.TrySetMaintenance(board, user, false));
        }
        catch (Exception exception)
        {
            Log.Error($"Optic commissioning failed: {exception}");
            return false;
        }
        finally { await OnMainAsync(() => _opticCommissioning.Remove(board)); }
    }
}
