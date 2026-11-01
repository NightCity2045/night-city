// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Text.Json;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed record InstallationSwap(NCMetaRuntimeInstance Runtime, NCMetaInstallation Previous, string Policy);
    private readonly Dictionary<(EntityUid Host, Guid Installation), InstallationSwap> _installationSwaps = [];

    private static string InstallationPolicy(NCMetaInstallation installation) =>
        NCMetaPersistenceSystem.Fingerprint(JsonSerializer.Serialize(installation, CommerceJson));

    // Main-thread only. Keep one swap per exact slot until its commit/reconciliation drains.
    private bool TryStopForInstallationSwap(EntityUid host, NCMetaInstallation? previous, out InstallationSwap? stopped)
    {
        stopped = null;
        if (previous == null) return true;
        if (_installationSwaps.ContainsKey((host, previous.Id))) return false;
        if (!_runtime.TryGetInstallation(host, previous.Owner, previous.Id, out var running)) return true;
        var policy = InstallationPolicy(previous);
        if (InstallationPolicy(running.Installation) != policy || _installationSwaps.Count >= 128) return false;
        stopped = new(running, previous, policy);
        _installationSwaps.Add((host, previous.Id), stopped);
        _runtime.StopInstallation(host, previous.Id);
        return true;
    }

    private void CancelInstallationRecovery(EntityUid host, Guid installationId) =>
        _installationSwaps.Remove((host, installationId));

    private async Task<NCMetaMutationOutcome> CommitInstallationSwapAsync(Context context,
        Func<Task<NCMetaMutationOutcome>> commit, Func<InstallationSwap?> getStopped)
    {
        NCMetaMutationOutcome? outcome = null;
        try { outcome = await commit(); return outcome; }
        finally
        {
            if (getStopped() is { } stopped)
            {
                if (outcome?.Success != true) await ReconcileInstallationAsync(context, stopped);
                await OnMainAsync(() =>
                {
                    var key = (stopped.Runtime.Host, stopped.Previous.Id);
                    if (_installationSwaps.TryGetValue(key, out var current) && ReferenceEquals(current, stopped))
                        _installationSwaps.Remove(key);
                    return true;
                });
            }
        }
    }

    private async Task ReconcileInstallationAsync(Context context, InstallationSwap stopped)
    {
        try { await RecoverInstallationAsync(context, stopped); }
        catch (Exception exception) { Log.Error($"META installation reconciliation failed for {stopped.Previous.Id}: {exception}"); }
    }

    private async Task RecoverInstallationAsync(Context context, InstallationSwap stopped)
    {
        // Fresh boot only: the stopped stack, handles and pending native intents remain revoked.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var old = stopped.Previous;
            var origin = stopped.Runtime;
            var durable = await _persistence.ReadInstallationAsync(old.HostDeviceId, old.Owner, old.Id);
            if (durable == null || InstallationPolicy(durable) != stopped.Policy) return;
            var authorization = await _marketplace.ResolveRunAuthorizationAsync(old.Owner, old.HostDeviceId, durable.Active);
            if (authorization == null) return;
            var artifact = await ReadVerifiedAsync(durable.Active);
            if (artifact == null) return;
            var service = await _authority.ResolveServiceAdmissionAsync(durable, artifact, unattended: false);
            if (service == null) return;
            var app = await ReadApplicationAsync(durable.Active, artifact);
            if (app.Declared && app.Application == null) return;
            durable = await _persistence.ReadInstallationAsync(old.HostDeviceId, old.Owner, old.Id);
            if (durable == null || InstallationPolicy(durable) != stopped.Policy) return;
            var admitted = durable;
            if (await OnMainAsync(() =>
            {
                if (!_installationSwaps.TryGetValue((origin.Host, old.Id), out var proof) || !ReferenceEquals(proof, stopped) ||
                    origin.WorldEpoch != context.Epoch || !IsCurrent(context) ||
                    !_devices.TryGetEntry(origin.Host, out var device) || device.Owner != old.Owner ||
                    device.AuthorityEpoch != origin.AuthorityEpoch || device.DeviceEpoch != origin.HostEpoch ||
                    !_runtime.CanReach(context.Terminal, origin.Host)) return true;
                if (_runtime.TryGetInstallation(origin.Host, old.Owner, old.Id, out _)) return true;
                if (!_runtime.TryStart(origin.Host, context.User, admitted, artifact, app.Application != null,
                        authorization.ExpiresAtTicks, authorization.Contact, service) ||
                    !_runtime.TryGetInstallation(origin.Host, old.Owner, old.Id, out var runtime)) return false;
                if (app.Application == null || _applications.TryRegisterRecovered(origin.Host, old.Owner, app.Application,
                    NCMetaAppPolicy.OwnerOnly with { GrantedScopes = service.GetGrantedScopes().ToImmutableHashSet(StringComparer.Ordinal) }, runtime.Generation)) return true;
                _runtime.StopInstallation(origin.Host, old.Id);
                return false;
            })) return;
            await Task.Delay(50);
        }
    }
}
