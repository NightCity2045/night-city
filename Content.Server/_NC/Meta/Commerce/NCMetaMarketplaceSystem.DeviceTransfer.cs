// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private async Task<NCAtomicLedgerParticipantWrite?> ReadTargetAuthorityWrite(string owner, string deviceId)
    {
        var nativeEpoch = await Main(() => _devices.TryResolve(deviceId, out var entry) && entry.Ready && entry.Owner == owner ? entry.AuthorityEpoch : 0);
        var device = nativeEpoch == 0 ? null : await _persistence.ReadDeviceAsync(deviceId);
        return device == null || device.Owner != owner || device.AuthorityEpoch != nativeEpoch ? null : new(Guid.NewGuid(),
            "meta.device.software-entitlement", "meta.device", deviceId, device.Revision, 1,
            JsonSerializer.Serialize(device with { Revision = checked(device.Revision + 1) }, Json), "{}");
    }

    /// <summary>Trusted physical-asset transaction input. Caller must atomically append installation grant/state revocation.</summary>
    public async Task<NCMetaDeviceTransferPreparation?> PrepareDeviceTransferAsync(string deviceId, string expectedOwner,
        string newOwner, ulong expectedAuthorityEpoch, Guid transferId, int maximumLicenses = 16)
    {
        if (!MetaSoftwarePolicy.Device(deviceId) || !ValidParty(expectedOwner) || !ValidParty(newOwner) || expectedOwner == newOwner ||
            transferId == Guid.Empty || maximumLicenses is < 0 or > 64 || expectedAuthorityEpoch is < 2 or >= ulong.MaxValue - 1) return null;
        var nativeEpoch = await Main(() => _devices.TryResolve(deviceId, out var entry) && entry.Ready && entry.Owner == expectedOwner &&
            entry.AuthorityEpoch == expectedAuthorityEpoch ? entry.DeviceEpoch : 0);
        if (nativeEpoch == 0) return null;
        var device = await _persistence.ReadDeviceAsync(deviceId);
        if (device == null || device.Owner != expectedOwner || device.AuthorityEpoch != expectedAuthorityEpoch) return null;
        var index = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId) ?? new(deviceId, [], 0);
        var oldOwner = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, expectedOwner) ?? new(expectedOwner, [], 0);
        var newOwnerIndex = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, newOwner) ?? new(newOwner, [], 0);
        var candidates = new List<MetaSoftwareLicense>();
        // Releasing a seat removes the device pointer, but a WithDevice entitlement remains bound to the asset.
        var ids = oldOwner.Licenses.Concat(index.Licenses.Values).Distinct().Order().ToArray();
        for (var offset = 0; offset < ids.Length; offset += 8)
        {
            var batch = await Task.WhenAll(ids.Skip(offset).Take(8).Select(id => Read<MetaSoftwareLicense>(LicenseType, id.ToString("D"))));
            foreach (var license in batch)
            {
                if (license == null) return null;
                if (index.Licenses.Any(value => value.Value == license.Id && value.Key != Key(license.ProductId, license.Major))) return null;
                if (license.Owner == expectedOwner && (license.Seats.Any(value => value.DeviceId == deviceId) ||
                    MetaSoftwarePolicy.CanTransferWithDevice(license, deviceId))) candidates.Add(license);
            }
        }
        if (candidates.Count > maximumLicenses) return null;
        var inheritedProof = await Read<NCMetaSoftwareInheritance>(InheritanceType, deviceId);
        var inheritedAt = await Main(() => _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1L);
        if (inheritedAt < 0) return null;
        var nextOld = oldOwner; var nextNew = newOwnerIndex;
        var writes = new List<NCAtomicLedgerParticipantWrite>();
        var retained = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var transferred = new List<Guid>();
        var previousLicenses = new Dictionary<Guid, MetaSoftwareLicense>();
        var transferredLicenses = new Dictionary<Guid, MetaSoftwareLicense>();
        foreach (var license in candidates.OrderBy(value => value.PurchasedAtTicks).ThenBy(value => value.Id))
        {
            var id = license.Id;
            var key = Key(license.ProductId, license.Major);
            var allocation = NCMetaPersistenceSystem.StableGuid("software-transfer-allocation:" + transferId + ":" + id);
            var prepared = MetaSoftwarePolicy.PrepareDeviceLicenseTransfer(license, deviceId, newOwner, allocation);
            if (prepared == null) return null;
            writes.Add(Write(LicenseType, id.ToString("D"), license.Revision, prepared.License));
            if (!prepared.OwnerChanged) continue;
            if (!nextOld.Licenses.Contains(id) || nextNew.Licenses.Contains(id) || nextNew.Licenses.Length >= 128) return null;
            nextOld = RemoveOwnerLicense(nextOld, license); nextNew = AddOwnerLicense(nextNew, prepared.License);
            // Prefer the currently attached license when more than one historical entitlement is bound to the asset.
            if (!retained.ContainsKey(key) || index.Licenses.GetValueOrDefault(key) == id ||
                index.Licenses.GetValueOrDefault(key) != retained[key]) retained[key] = id;
            transferred.Add(id);
            previousLicenses.Add(id, license);
            transferredLicenses.Add(id, prepared.License);
        }
        // Each owner aggregate advances once, irrespective of how many product seats move together.
        if (transferred.Count > 0)
        {
            writes.Add(Write(OwnerType, expectedOwner, oldOwner.Revision, nextOld with { Revision = checked(oldOwner.Revision + 1) }));
            writes.Add(Write(OwnerType, newOwner, newOwnerIndex.Revision, nextNew with { Revision = checked(newOwnerIndex.Revision + 1) }));
        }
        writes.Add(Write(DeviceType, deviceId, index.Revision, index with { Licenses = retained, Revision = checked(index.Revision + 1) }));
        var nextAuthority = expectedAuthorityEpoch + 1;
        var inherited = await PrepareInheritedGrants(deviceId, expectedOwner, expectedAuthorityEpoch, inheritedAt,
            retained, previousLicenses, transferredLicenses, inheritedProof);
        if (inherited == null) return null;
        writes.Add(Write(InheritanceType, deviceId, inheritedProof?.Revision ?? 0, new NCMetaSoftwareInheritance(deviceId,
            newOwner, nextAuthority, transferId, inheritedAt, inherited, checked((inheritedProof?.Revision ?? 0) + 1))));
        var nextDevice = device with { Owner = newOwner, Projects = [], ActiveProject = Guid.Empty, AuthorityEpoch = nextAuthority, Revision = checked(device.Revision + 1) };
        var deviceWrite = new NCPersistentAggregateWrite(Guid.NewGuid(), "meta.device.transferred", "meta.device", deviceId,
            device.Revision, 1, JsonSerializer.Serialize(nextDevice, Json), JsonSerializer.Serialize(new { transferId, expectedOwner, newOwner, nextAuthority }, Json));
        var participants = writes.Select(value => new NCPersistentAggregateWrite(value.EventId, value.EventType, value.AggregateType,
            value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotFormatVersion, value.SnapshotPayload, value.EventPayload)).Append(deviceWrite).ToArray();
        if (await _persistence.ReadReservedSoftwareCopyBytesAsync(deviceId, expectedOwner) == null) return null;
        // The enclosing installation-custody participant owns the single physical copy-reservation reset.
        var fingerprint = MetaSoftwarePolicy.Fingerprint(transferId.ToString("D"), deviceId, expectedOwner, newOwner,
            expectedAuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.Serialize(participants.Select(value => new
            { value.AggregateType, value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotPayload }), Json));
        var result = new NCMetaDeviceTransferPreparation(deviceId, expectedOwner, newOwner, nativeEpoch, expectedAuthorityEpoch,
            nextAuthority, transferId, fingerprint, transferred.ToArray(), participants)
            { PreserveInstallationIds = inherited.Select(value => value.InstallationId).ToArray() };
        return await Main(() => CanCommitDeviceTransfer(result)) ? result : null;
    }

    /// <summary>Main-thread guard immediately before the enclosing authoritative physical transfer is committed.</summary>
    public bool CanCommitDeviceTransfer(NCMetaDeviceTransferPreparation preparation) =>
        _devices.TryResolve(preparation.DeviceId, out var entry) && entry.Ready && entry.DeviceEpoch == preparation.DeviceEpoch &&
        entry.Owner == preparation.PreviousOwner && entry.AuthorityEpoch == preparation.ExpectedAuthorityEpoch &&
        preparation.NextAuthorityEpoch == preparation.ExpectedAuthorityEpoch + 1;

    /// <summary>Call only after outer atomic success; durable ownership is re-read before changing live authority.</summary>
    public async Task<bool> TryApplyCommittedDeviceTransferAsync(NCMetaDeviceTransferPreparation preparation)
    {
        var durable = await _persistence.ReadDeviceAsync(preparation.DeviceId);
        if (durable == null || durable.Owner != preparation.NewOwner || durable.AuthorityEpoch != preparation.NextAuthorityEpoch) return false;
        return await Main(() =>
        {
            if (!_devices.TryResolve(preparation.DeviceId, out var entry) || !entry.Ready) return false;
            if (entry.Owner == preparation.NewOwner && entry.AuthorityEpoch == preparation.NextAuthorityEpoch) return true;
            if (entry.Owner != preparation.PreviousOwner || entry.AuthorityEpoch != preparation.ExpectedAuthorityEpoch ||
                !_devices.TryApplyOwner(entry.Entity, entry.Id, preparation.PreviousOwner,
                    preparation.NewOwner, preparation.NextAuthorityEpoch)) return false;
            LicenseDeviceRevoked?.Invoke(entry.Id);
            return true;
        });
    }
}
