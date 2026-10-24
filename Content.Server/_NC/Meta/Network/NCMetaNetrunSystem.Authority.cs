// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Persistence;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    [Dependency] private NCMetaAuthoritySystem _configurationAuthority = default!;
    [Dependency] private NCMetaSnapshotSystem _netSnapshots = default!;
    [Dependency] private ITaskManager _configurationTasks = default!;

    private sealed record ConfigurationDevice(EntityUid Entity, string Id, string Owner, ulong Epoch, ulong Authority);
    private sealed record ConfigurationSeed(EntityUid Actor, EntityUid Origin, EntityUid Access,
        string Owner, ConfigurationDevice[] Devices, bool RequireTargetRoutes, NCMetaManagementProof Management);
    private sealed record ConfigurationAuthorization(ConfigurationSeed Seed, NCMetaManagementProof Management,
        NCMetaAuthorityConstraints Constraints);

    // Main-thread capture only. The caller has already reserved its bounded policy operation and mutation lease.
    private ConfigurationSeed? CaptureConfiguration(EntityUid actor, EntityUid origin, string accessId,
        IEnumerable<EntityUid> devices, bool requireTargetRoutes)
    {
        if (!_nativeAccepting || !_devices.TryResolve(accessId, out var access) || access.Owner.Length == 0 ||
            !_devices.CanManage(origin, actor) ||
            !_configurationAuthority.TryGetManagementProof(actor, access.Owner, out var management)) return null;
        var entities = devices.Append(access.Entity).Append(origin).Distinct().Take(66).ToArray();
        if (entities.Length is < 1 or > 65) return null;
        var entries = new List<ConfigurationDevice>(entities.Length);
        foreach (var entity in entities)
        {
            if (!_devices.TryGetEntry(entity, out var device) || !device.Ready || device.Owner != access.Owner ||
                device.AuthorityEpoch < 2 || !_devices.CanManage(entity, actor, requireRange: false)) return null;
            if (requireTargetRoutes && !_network.CanRoute(origin, entity)) return null;
            entries.Add(new(entity, device.Id, device.Owner, device.DeviceEpoch, device.AuthorityEpoch));
        }
        return new(actor, origin, access.Entity, access.Owner, entries.ToArray(), requireTargetRoutes, management);
    }

    private async Task<ConfigurationAuthorization?> AuthorizeConfigurationAsync(ConfigurationSeed seed)
    {
        var management = seed.Management;
        return await ConfigurationMain(() =>
        {
            if (!IsConfigurationCurrent(seed, management)) return null;
            var constraints = _configurationAuthority.CaptureWriteConstraints(management,
                seed.Devices.Select(device => device.Entity).ToArray());
            return constraints == null ? null : new ConfigurationAuthorization(seed, management, constraints);
        });
    }

    private bool IsConfigurationCurrent(ConfigurationSeed seed, NCMetaManagementProof management)
    {
        if (!_nativeAccepting || !_configurationAuthority.IsCurrent(management) ||
            !_devices.CanManage(seed.Origin, seed.Actor)) return false;
        foreach (var captured in seed.Devices)
        {
            if (!_devices.TryGetEntry(captured.Entity, out var current) || !current.Ready ||
                current.Id != captured.Id || current.Owner != captured.Owner ||
                current.DeviceEpoch != captured.Epoch || current.AuthorityEpoch != captured.Authority ||
                !_devices.CanManage(captured.Entity, seed.Actor, requireRange: false)) return false;
            if (seed.RequireTargetRoutes && !_network.CanRoute(seed.Origin, captured.Entity)) return false;
        }
        return true;
    }

    private Task<T> ConfigurationMain<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _configurationTasks.RunOnMainThread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }
}
