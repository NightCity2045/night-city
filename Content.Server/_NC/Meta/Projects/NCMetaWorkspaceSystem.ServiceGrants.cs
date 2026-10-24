// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private async Task ServiceGrantRequestAsync(Context context, NCMetaProject project,
        NCMetaWorkspaceRequestMessage request, NCMetaWorkspaceState state)
    {
        var target = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
        var host = await OnMainAsync<EntityUid?>(() => IsCurrent(context) && _devices.TryResolve(target, out var device) &&
            device.Owner == context.Owner && device.Ready && _runtime.CanReach(context.Terminal, device.Entity)
                ? device.Entity : null);
        if (host == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var installation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
        if (installation == null) { state.Notice = Loc.GetString("nc-meta-error-action"); return; }
        var artifact = await ReadVerifiedAsync(installation.Active);
        if (artifact == null) { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
        if (request.Operation == NCMetaWorkspaceOperation.IssueServiceGrants)
        {
            NCMetaGrantApprovalRequest? approval;
            try { approval = JsonSerializer.Deserialize<NCMetaGrantApprovalRequest>(request.Source, new JsonSerializerOptions { MaxDepth = 8 }); }
            catch (JsonException) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
            if (approval == null) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
            var result = await _authority.TryIssueInstallationGrantsAsync(context.Terminal, context.User, host.Value,
                request.RequestId, installation, artifact, approval);
            state.Notice = Loc.GetString(result.MessageKey);
        }
        else if (request.Operation == NCMetaWorkspaceOperation.RevokeServiceGrant)
        {
            var result = await _authority.TryRevokeGrantAsync(context.Terminal, context.User, host.Value,
                request.RequestId, target, installation.Id, request.FileId, request.BaseRevision);
            state.Notice = Loc.GetString(result.MessageKey);
        }
        if (!await OnMainAsync(() => IsCurrent(context))) return;
        state.ServiceGrants = await _authority.ReadGrantPolicyAsync(context.Terminal, context.User, target, installation.Id, artifact);
        if (state.ServiceGrants == null) state.Notice = Loc.GetString("nc-meta-error-access");
    }
}
