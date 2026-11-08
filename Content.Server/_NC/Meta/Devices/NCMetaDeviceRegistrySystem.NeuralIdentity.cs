// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared._NC.Meta;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Devices;

public sealed partial class NCMetaDeviceRegistrySystem
{
    /// <summary>Restoring a removable controller restores its durable software, never a new owner's authority.</summary>
    public bool TryRestoreNeuralIdentity(EntityUid uid)
    {
        if (!HasComp<NCNeuralControllerComponent>(uid) || !TryComp<NCCyberwarePartComponent>(uid, out var part) ||
            part.StableInstanceId == Guid.Empty || !TryComp<NCMetaHostComponent>(uid, out var host) ||
            !TryComp<NCMetaDeviceComponent>(uid, out var device)) return false;
        var id = NCMetaPersistenceSystem.StableGuid("neural-controller:" + part.StableInstanceId.ToString("D")).ToString("D");
        if (TryGetEntry(uid, out var entry) && entry.Id == id) return true;
        if (_devices.ContainsKey(id)) return false;
        Remove(uid);
        host.DeviceId = device.DeviceId = id;
        host.OwnerKey = device.OwnerKey = string.Empty;
        Register(uid, ref host.DeviceId, string.Empty);
        return true;
    }
}
