// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    public event Action<string>? LicenseDeviceRevoked;
    public event Action<Guid, int, string>? EntitlementChanged;

    private async Task<(bool Found, bool Conflict, NCMetaSoftwareCommand? Receipt)> ReadCommand(Context context, Guid request, string operation, string fingerprint, Guid expectedLicense)
    {
        var command = NCPersistentIdentity.Command("meta", "software-" + operation, context.Actor + ":" + request);
        var stored = await ReadAcknowledgement(context, request, operation, fingerprint);
        if (stored.Found) return (true, stored.Value?.Command == null || stored.Value.Command.LicenseId != expectedLicense, stored.Value?.Command);
        var replay = await Read<NCMetaSoftwareCommand>(CommandType, command.ToString("D"));
        return (replay != null, replay != null && (replay.Fingerprint != fingerprint || replay.LicenseId != expectedLicense), replay);
    }
    private static NCAtomicLedgerParticipantWrite CommandWrite(Context context, Guid request, string operation,
        string fingerprint, Guid license, Guid transaction = default, NCMetaSoftwareResaleOffer? offer = null) => Write(CommandType,
        NCPersistentIdentity.Command("meta", "software-" + operation, context.Actor + ":" + request).ToString("D"), 0,
        new NCMetaSoftwareCommand(fingerprint, license, transaction, 1) { ResaleOffer = offer });

    public async Task<NCMetaSoftwareOutcome> TryAssignSeatAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid licenseId, string deviceId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || !MetaSoftwarePolicy.Device(deviceId)) return Failure("access");
        var license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
        if (license == null) return Failure("access");
        var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseId.ToString("D"), deviceId);
        var replay = await ReadCommand(context, requestId, "seat-assign", fingerprint, licenseId);
        if (replay.Conflict) return Failure("conflict");
        if (replay.Found) return new(true, "nc-meta-software-seat-assigned", License: await PresentLicense(context, license));
        if (license.Revoked || !await CanActFor(context, license.Owner)) return Failure("access");
        if (license.Seats.Any(seat => seat.DeviceId == deviceId)) return new(true, "nc-meta-software-seat-assigned", License: await PresentLicense(context, license));
        if (!MetaSoftwarePolicy.CanAssignSeat(license, deviceId, context.Time.Ticks)) return Failure("quota");
        var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId) ?? new(deviceId, [], 0);
        var key = Key(license.ProductId, license.Major);
        if (device.Licenses.TryGetValue(key, out var previousId))
        {
            var previous = await Read<MetaSoftwareLicense>(LicenseType, previousId.ToString("D"));
            if (previous is { Revoked: false } && previous.Owner == license.Owner && previous.Seats.Any(seat => seat.DeviceId == deviceId)) return Failure("already-owned");
        }
        else if (device.Licenses.Count >= 64) return Failure("quota");
        if (!await Main(() => Current(context) && _devices.TryResolve(deviceId, out var target) && target.Owner == license.Owner)) return Failure("access");
        var authority = await ReadTargetAuthorityWrite(license.Owner, deviceId);
        if (authority == null) return Failure("access");
        var assigned = license with { Seats = [.. license.Seats, new MetaSoftwareSeat(Guid.NewGuid(), deviceId)], Revision = license.Revision + 1 };
        var entries = new Dictionary<string, Guid>(device.Licenses, StringComparer.Ordinal) { [key] = license.Id };
        return await Batch(context, requestId, "seat-assign", fingerprint,
            [Write(LicenseType, licenseId.ToString("D"), license.Revision, assigned), Write(DeviceType, deviceId, device.Revision, device with { Licenses = entries, Revision = device.Revision + 1 }),
                authority, CommandWrite(context, requestId, "seat-assign", fingerprint, licenseId)])
            ? new(true, "nc-meta-software-seat-assigned", License: await PresentLicense(context, assigned)) : Failure("conflict");
    }

    public async Task<NCMetaSoftwareOutcome> TryReleaseSeatAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid licenseId, string deviceId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || !MetaSoftwarePolicy.Device(deviceId)) return Failure("access");
        var license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
        if (license == null) return Failure("access");
        var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseId.ToString("D"), deviceId);
        var replay = await ReadCommand(context, requestId, "seat-release", fingerprint, licenseId);
        if (replay.Conflict) return Failure("conflict");
        var seat = license.Seats.SingleOrDefault(seat => seat.DeviceId == deviceId);
        if (replay.Found) return new(true, "nc-meta-software-seat-released", License: await PresentLicense(context, license));
        if (license.Revoked || !await CanActFor(context, license.Owner)) return Failure("access");
        if (seat == null) return new(true, "nc-meta-software-seat-released", License: await PresentLicense(context, license));
        var updated = license with { Seats = license.Seats.Where(seat => seat.DeviceId != deviceId).ToArray(), Activations = license.Activations.Where(receipt => receipt.AllocationId != seat.AllocationId).ToArray(), Revision = license.Revision + 1 };
        var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId);
        var writes = new List<NCAtomicLedgerParticipantWrite> { Write(LicenseType, licenseId.ToString("D"), license.Revision, updated), CommandWrite(context, requestId, "seat-release", fingerprint, licenseId) };
        if (device != null && device.Licenses.GetValueOrDefault(Key(license.ProductId, license.Major)) == licenseId)
        {
            var entries = new Dictionary<string, Guid>(device.Licenses, StringComparer.Ordinal); entries.Remove(Key(license.ProductId, license.Major));
            writes.Add(Write(DeviceType, deviceId, device.Revision, device with { Licenses = entries, Revision = device.Revision + 1 }));
        }
        if (!await Batch(context, requestId, "seat-release", fingerprint, writes)) return Failure("conflict");
        await Main(() => { RevokeDevice(deviceId); EntitlementChanged?.Invoke(license.ProductId, license.Major, license.Owner); return true; });
        return new(true, "nc-meta-software-seat-released", License: await PresentLicense(context, updated));
    }

    /// <summary>Trusted broker input. The returned write must commit atomically with the installation pointer.</summary>
    public async Task<NCMetaSoftwareActivationAdmission?> PrepareActivationAsync(string owner, string deviceId,
        Guid productId, Guid releaseId, bool consentToNewScopes, string? accessDeviceId = null)
    {
        var online = await Main(() => CanContactMarketplace(owner, deviceId, accessDeviceId));
        var resolved = await ResolveLicensedReleaseAsync(owner, deviceId, productId, releaseId);
        if (resolved == null) return null;
        var capture = await Main(() => _devices.TryResolve(deviceId, out var device) && device.Owner == owner &&
            _persistence.TryCaptureTime(out _, out var time) ? (Authority: device.AuthorityEpoch, Ticks: time.Ticks) : (Authority: 0UL, Ticks: -1L));
        if (capture.Authority == 0 || capture.Ticks < 0) return null;
        var license = resolved.Entitlement;
        if (license == null) return online && (resolved.Descriptor.RequiredScopes.Length == 0 || consentToNewScopes) ? new(resolved, null, capture.Authority) : null;
        var seat = license.Seats.SingleOrDefault(seat => seat.DeviceId == deviceId);
        if (seat == null) return null;
        var previous = license.Activations.LastOrDefault(item => item.DeviceId == deviceId && item.AllocationId == seat.AllocationId);
        if (MetaSoftwarePolicy.ScopeDiff(previous?.ApprovedScopes ?? [], resolved.Descriptor.RequiredScopes).RequiresConsent && !consentToNewScopes) return null;
        var offline = online ? null : await PrepareOfflineActivation(resolved, owner, deviceId, capture.Authority, capture.Ticks, consentToNewScopes);
        if (!online && offline == null) return null;
        var receipt = (offline?.Receipt ?? new MetaSoftwareActivation(seat.AllocationId, deviceId, owner, capture.Authority,
            resolved.Release.Digest, capture.Ticks, capture.Ticks)) with { ApprovedScopes = resolved.Descriptor.RequiredScopes.Order(StringComparer.Ordinal).ToArray() };
        var retained = license.Activations.Where(item => item.DeviceId == deviceId && item.ReleaseDigest != receipt.ReleaseDigest).TakeLast(1);
        var updated = license with { Activations = license.Activations.Where(item => item.DeviceId != deviceId).Concat(retained).Append(receipt).ToArray(), Revision = checked(license.Revision + 1) };
        var write = new NCPersistentAggregateWrite(Guid.NewGuid(), "meta.software.activated", LicenseType, license.Id.ToString("D"), license.Revision, 1,
            JsonSerializer.Serialize(updated, Json), JsonSerializer.Serialize(new { license.Id, DeviceId = deviceId, ReleaseId = releaseId, receipt.ReleaseDigest, receipt.ApprovedScopes, receipt.DeviceAuthorityEpoch }, Json));
        return await Main(() => _devices.TryResolve(deviceId, out var current) && current.Owner == owner && current.AuthorityEpoch == capture.Authority &&
            _persistence.TryCaptureTime(out _, out var currentTime) && (online ? CanContactMarketplace(owner, deviceId, accessDeviceId) :
                license.Terms.Offline != MetaOfflinePolicy.GracePeriod || currentTime.Ticks >= receipt.LastOnlineTicks &&
                currentTime.Ticks - receipt.LastOnlineTicks <= license.Terms.OfflineGraceTicks))
            ? new(resolved, write, capture.Authority) { RequiredInheritedInstallationId = offline?.InheritedInstallation } : null;
    }

    public async Task<NCMetaSoftwareOutcome> TryReadSourceAsync(EntityUid terminal, EntityUid user, Guid productId, Guid releaseId,
        string path, Guid? licenseId = null, string? licenseOwner = null)
    {
        var context = await Main(() => Capture(terminal, user));
        if (context == null || !MetaPaths.IsValid(path)) return Failure("access");
        var owner = licenseOwner ?? context.Actor;
        if (!await CanActFor(context, owner)) return Failure("access");
        var locator = await Read<NCMetaSoftwareReleaseLocator>("meta.software.release-index", productId.ToString("D") + ":" + releaseId.ToString("D"));
        var product = locator == null ? null : await ReadProductAsync(productId, locator.Major);
        var entitlement = licenseId is { } id ? await Read<MetaSoftwareLicense>(LicenseType, id.ToString("D")) : null;
        if (product == null || !MetaSoftwarePolicy.CanReadSource(product, entitlement, owner, context.Time.Ticks)) return Failure("source-denied");
        var publication = await Read<NCMetaSoftwarePublication>(PublicationType, Key(productId, product.Major) + ":" + releaseId.ToString("D"));
        var file = publication?.Release.Sources?.SingleOrDefault(file => file.Path == path);
        if (file is not { Bytes: >= 0 and <= 262144 }) return Failure("source-denied");
        var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
        if (bytes == null || bytes.Length != file.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Digest) return Failure("storage");
        if (!await CanActFor(context, owner)) return Failure("access");
        entitlement = licenseId is { } currentId ? await Read<MetaSoftwareLicense>(LicenseType, currentId.ToString("D")) : null;
        var currentTime = await Main(() => Current(context) && _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1);
        if (currentTime < 0 || !MetaSoftwarePolicy.CanReadSource(product, entitlement, owner, currentTime)) return Failure("source-denied");
        try { return new(true, "nc-meta-software-source-ready", Source: new UTF8Encoding(false, true).GetString(bytes)); }
        catch (DecoderFallbackException) { return Failure("storage"); }
    }

    private void RevokeDevice(string deviceId)
    {
        if (_devices.TryResolve(deviceId, out var device)) _devices.TryInvalidate(device.Entity);
        LicenseDeviceRevoked?.Invoke(deviceId);
    }
}
