// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    /// <summary>Read-only main-thread check of the exact native bridge and the currently physical service route.</summary>
    public bool CanMaintainRunContact(NCMetaRunContactRequirement proof) =>
        _persistence.TryCaptureTime(out var epoch, out _) && epoch == proof.WorldEpoch &&
        _devices.TryResolve(proof.RunDeviceId, out var host) && host.Ready && host.Owner == proof.Owner && host.AuthorityEpoch == proof.RunAuthorityEpoch &&
        _devices.TryResolve(proof.AccessDeviceId, out var access) && access.Ready && access.Owner == proof.Owner &&
        access.DeviceEpoch == proof.AccessDeviceEpoch && access.AuthorityEpoch == proof.AccessAuthorityEpoch &&
        CanContactMarketplace(proof.Owner, proof.RunDeviceId, proof.AccessDeviceId);

    internal static bool HasOfflineRunGrace(NCMetaRunContactRequirement proof, long lastOnlineTicks, long nowTicks) =>
        proof.Policy == MetaOfflinePolicy.GracePeriod && proof.GraceTicks > 0 && lastOnlineTicks >= 0 &&
        nowTicks >= lastOnlineTicks && nowTicks - lastOnlineTicks <= proof.GraceTicks;

    /// <summary>Bounded native heartbeat caller only. Renewal preserves the allocation, scopes and image identity.</summary>
    internal async Task<bool> TryRenewRunContactAsync(NCMetaRunContactRequirement proof)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || !await Main(() => CanMaintainRunContact(proof))) return false;
        var license = await Read<MetaSoftwareLicense>(LicenseType, proof.LicenseId.ToString("D"));
        var receipt = license?.Activations.SingleOrDefault(value => value.AllocationId == proof.AllocationId &&
            value.DeviceId == proof.RunDeviceId && value.Owner == proof.Owner && value.DeviceAuthorityEpoch == proof.RunAuthorityEpoch &&
            value.ReleaseDigest == proof.ReleaseDigest);
        if (license == null || license.Owner != proof.Owner || license.Revoked || receipt == null || license.Terms.Offline != proof.Policy ||
            license.Terms.OfflineGraceTicks != proof.GraceTicks) return false;
        var now = await Main(() => CanMaintainRunContact(proof) && _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1);
        if (now < receipt.LastOnlineTicks || license.IllegalExpiresAtTicks is { } expiry && now >= expiry) return false;
        if (now == receipt.LastOnlineTicks) return true;
        var device = await _persistence.ReadDeviceAsync(proof.RunDeviceId);
        if (device == null || device.Owner != proof.Owner || device.AuthorityEpoch != proof.RunAuthorityEpoch) return false;
        var updated = license with { Activations = license.Activations.Select(value => value == receipt ? value with { LastOnlineTicks = now } : value).ToArray(),
            Revision = checked(license.Revision + 1) };
        if (!await Main(() => CanMaintainRunContact(proof))) return false;
        var command = Guid.NewGuid();
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(command, "meta.software.contact",
            "meta.owner", proof.Owner,
            [new(Guid.NewGuid(), "meta.software.contact-renewed", LicenseType, license.Id.ToString("D"), license.Revision, 1,
                JsonSerializer.Serialize(updated, Json), "{}"),
                new(Guid.NewGuid(), "meta.device.contact-attested", "meta.device", device.DeviceId, device.Revision, 1,
                    JsonSerializer.Serialize(device with { Revision = checked(device.Revision + 1) }, Json), "{}")],
            "{}", command, null, proof.WorldEpoch, new(now), [], RequestFingerprint: MetaSoftwarePolicy.Fingerprint(
                proof.Owner, proof.LicenseId.ToString("D"), proof.AllocationId.ToString("D"), proof.ReleaseDigest,
                now.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
}
