// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Devices;

public sealed partial class NCMetaDeviceRegistrySystem
{
    private readonly Dictionary<string, Guid> _ownerReconciliation = new(StringComparer.Ordinal);

    /// <summary>Server thread only. Suspends every native entry point before the enclosing durable commit.</summary>
    public bool TrySuspendForOwnerReconciliation(string id, Guid operationId)
    {
        if (operationId == Guid.Empty || !TryResolve(id, out var entry) || !entry.Ready || _ownerReconciliation.ContainsKey(id)) return false;
        _ownerReconciliation.Add(id, operationId);
        entry.Ready = false;
        entry.DeviceEpoch = entry.DeviceEpoch == ulong.MaxValue ? ulong.MaxValue : entry.DeviceEpoch + 1;
        Changed?.Invoke(entry);
        return true;
    }

    /// <summary>Only a fresh durable read may restore a suspended entry, including after an uncertain outcome.</summary>
    public bool TryReconcileDurableOwner(string id, Guid operationId, NCMetaDeviceRecord durable)
    {
        if (!_devices.TryGetValue(id, out var entry) || !Exists(entry.Entity) || durable.DeviceId != id ||
            durable.AuthorityEpoch < entry.AuthorityEpoch) return false;
        if (_ownerReconciliation.TryGetValue(id, out var pending) && pending != operationId) return false;
        if (!_ownerReconciliation.ContainsKey(id))
        {
            if (entry.Ready && entry.Owner == durable.Owner && entry.AuthorityEpoch == durable.AuthorityEpoch) return true;
        }
        entry.Ready = false;
        entry.Available = true;
        entry.DeviceEpoch = entry.DeviceEpoch == ulong.MaxValue ? ulong.MaxValue : entry.DeviceEpoch + 1;
        Changed?.Invoke(entry);
        if (!TryApplyOwner(entry.Entity, id, entry.Owner, durable.Owner, durable.AuthorityEpoch)) return false;
        _ownerReconciliation.Remove(id);
        entry.Ready = true;
        Changed?.Invoke(entry);
        return true;
    }
}
