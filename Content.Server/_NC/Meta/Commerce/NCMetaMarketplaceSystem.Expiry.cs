// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private sealed record IllegalExpiry(Guid LicenseId, Guid ProductId, int Major, string Owner, string DeviceId, ulong AuthorityEpoch, long Ticks);
    private readonly Dictionary<Guid, IllegalExpiry> _illegalExpiry = [];
    private readonly PriorityQueue<IllegalExpiry, long> _illegalDeadlines = new();

    private bool TrackIllegalExpiry(MetaSoftwareLicense license, string deviceId, ulong authorityEpoch)
    {
        if (license.Provenance != MetaLicenseProvenance.NativePiracy) return true;
        if (license.IllegalExpiresAtTicks is not { } ticks) return false;
        if (_illegalExpiry.TryGetValue(license.Id, out var existing))
            return existing.Ticks == ticks && existing.DeviceId == deviceId && existing.Owner == license.Owner && existing.AuthorityEpoch == authorityEpoch;
        if (_illegalExpiry.Count >= 4096 || _illegalDeadlines.Count >= 8192) return false;
        var expiry = new IllegalExpiry(license.Id, license.ProductId, license.Major, license.Owner, deviceId, authorityEpoch, ticks);
        _illegalExpiry.Add(license.Id, expiry); _illegalDeadlines.Enqueue(expiry, ticks);
        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out var time)) return;
        for (var work = 0; work < 32 && _illegalDeadlines.TryPeek(out _, out var deadline) && deadline <= time.Ticks; work++)
        {
            var expiry = _illegalDeadlines.Dequeue();
            if (!_illegalExpiry.Remove(expiry.LicenseId, out var current) || current != expiry) continue;
            try
            {
                if (_devices.TryResolve(expiry.DeviceId, out var device) && device.Owner == expiry.Owner && device.AuthorityEpoch == expiry.AuthorityEpoch)
                    RevokeDevice(expiry.DeviceId);
                EntitlementChanged?.Invoke(expiry.ProductId, expiry.Major, expiry.Owner);
            }
            catch (Exception exception) { Log.Error($"META illegal entitlement expiry cleanup failed: {exception.GetType().Name}."); }
        }
    }
}
