// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Runtime;

public sealed partial class NCMetaRuntimeSystem
{
    // Only authorized expiring runtimes enter this index. Sleeping permanent installations are never scanned.
    private const int MaximumExpiringRuntimes = 4096;
    private readonly SortedSet<(long Ticks, Guid Generation)> _entitlementDeadlines = [];
    private readonly Dictionary<Guid, NCMetaRuntimeInstance> _entitlementOwners = [];

    private bool HasRunEntitlement(NCMetaRuntimeInstance instance) => instance.EntitlementExpiresAtTicks is not { } deadline ||
        _persistence.TryCaptureTime(out var epoch, out var now) && epoch == instance.WorldEpoch && now.Ticks < deadline;

    private bool TryTrackEntitlement(NCMetaRuntimeInstance instance)
    {
        if (instance.EntitlementExpiresAtTicks is not { } deadline) return true;
        if (_entitlementOwners.Count >= MaximumExpiringRuntimes || !HasRunEntitlement(instance) ||
            !_entitlementOwners.TryAdd(instance.Generation, instance)) return false;
        _entitlementDeadlines.Add((deadline, instance.Generation)); return true;
    }

    private void UntrackEntitlement(NCMetaRuntimeInstance instance)
    {
        if (!_entitlementOwners.Remove(instance.Generation) || instance.EntitlementExpiresAtTicks is not { } deadline) return;
        _entitlementDeadlines.Remove((deadline, instance.Generation));
    }

    private void StopExpiredEntitlements(NCSessionEpoch epoch, NCActiveWorldTime now)
    {
        // This maximum equals total scheduler admission, so all due executions stop before the next VM phase.
        for (var work = 0; work < MaximumExpiringRuntimes && _entitlementDeadlines.Count != 0; work++)
        {
            var next = _entitlementDeadlines.Min;
            if (next.Ticks > now.Ticks) break;
            _entitlementDeadlines.Remove(next);
            if (_entitlementOwners.Remove(next.Generation, out var instance))
                Stop(instance, instance.WorldEpoch == epoch ? MetaStopReason.PermissionRevoked : MetaStopReason.Stop);
        }
    }
}
