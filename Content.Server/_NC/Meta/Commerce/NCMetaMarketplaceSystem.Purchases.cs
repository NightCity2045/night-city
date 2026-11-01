// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.IO;
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    public async Task<NCMetaSoftwareOutcome> TryQuoteAsync(EntityUid terminal, EntityUid user, Guid requestId,
        Guid productId, int major, Guid releaseId, string licenseOwner, MetaLicenseScope scope, int seatCount, string[] deviceIds,
        string slotKind = "User")
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || slotKind is not ("User" or "Firmware") || !Enum.IsDefined(scope) || seatCount is < 1 or > 64 || deviceIds == null || deviceIds.Length > 6 || deviceIds.Length > seatCount || deviceIds.Distinct(StringComparer.Ordinal).Count() != deviceIds.Length || deviceIds.Any(device => !MetaSoftwarePolicy.Device(device))) return Failure("access");
        var targets = deviceIds.Order(StringComparer.Ordinal).ToArray();
        var quoteId = NCMetaPersistenceSystem.StableGuid("software-quote:" + context.Actor + ":" + requestId);
        var input = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseOwner, productId.ToString("D"), major.ToString(CultureInfo.InvariantCulture),
            releaseId.ToString("D"), scope.ToString(), seatCount.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(targets), slotKind);
        var remembered = await ReadAcknowledgement(context, requestId, "quote", input);
        if (remembered.Found) return remembered.Value?.Quote is { } priorQuote
            ? new(true, "nc-meta-software-quote-ready", Quote: priorQuote) : Failure("conflict");
        var old = await Read<NCMetaSoftwareQuote>(QuoteType, quoteId.ToString("D"));
        if (old != null) return old.Buyer == context.Actor && old.LicenseOwner == licenseOwner && old.ProductId == productId && old.Major == major && old.ReleaseId == releaseId && old.Scope == scope && old.SeatCount == seatCount && old.SlotKind == slotKind && old.DeviceIds.SequenceEqual(targets) ? new(true, "nc-meta-software-quote-ready", Quote: old) : Failure("conflict");
        if (!await CanActFor(context, licenseOwner)) return Failure("access");
        var product = await ReadProductAsync(productId, major);
        var release = product?.Releases.SingleOrDefault(release => release.Id == releaseId);
        if (product is not { Offered: true } || release == null || scope == MetaLicenseScope.Device && (seatCount != 1 || targets.Length != 1) || scope == MetaLicenseScope.Organization && (!product.Terms.AllowOrganizationSeats || !NCPartyRef.TryParse(licenseOwner, out var organization) || organization.Kind != NCPartyKind.Organization)) return Failure("input");
        var compatibility = await Task.WhenAll(targets.Select(id => Compatibility(licenseOwner, id, release, slotKind)));
        if (compatibility.Any(value => value == null)) return Failure("access");
        if (compatibility.Any(value => !value!.Compatible)) return Failure("incompatible");
        if (!await Main(() => Current(context) && targets.All(id => _devices.TryResolve(id, out var device) && device.Owner == licenseOwner))) return Failure("access");
        var price = product.Publisher == licenseOwner ? 0 : checked(product.Price * seatCount);
        var freshTime = await Main(() => Current(context) && _persistence.TryCaptureTime(out _, out var time) ? (long?)time.Ticks : null);
        if (freshTime == null) return Failure("access");
        var expires = checked(freshTime.Value + TimeSpan.FromMinutes(2).Ticks);
        var slot = await _persistence.PrepareQuoteAsync("software", context.Actor, quoteId, freshTime.Value, expires);
        if (slot == null) return Failure("quota");
        var quote = new NCMetaSoftwareQuote(quoteId, context.Actor, licenseOwner, productId, major, releaseId, release.ArtifactDigest, scope, seatCount, targets, price, MetaSoftwarePolicy.TermsFingerprint(product.Terms), product.Revision, expires, false, Guid.Empty, slot.PreviousSlotRevision + 1)
        { Compatibility = compatibility.Cast<MetaSoftwareCompatibility>().ToArray(), SlotKind = slotKind, SlotAware = true, PoolSlot = slot.Slot };
        var pool = slot.PoolWrite;
        if (!await Batch(context, requestId, "quote", input,
                [new(pool.EventId, pool.EventType, pool.AggregateType, pool.AggregateId, pool.ExpectedAggregateVersion, pool.SnapshotFormatVersion, pool.SnapshotPayload, pool.EventPayload),
                    Write(QuoteType, slot.StorageKey, slot.PreviousSlotRevision, quote)], Acknowledgement(context, "quote", input, [], quote))) return Failure("conflict");
        var committed = await ReadAcknowledgement(context, requestId, "quote", input);
        return committed.Value?.Quote is { } ready ? new(true, "nc-meta-software-quote-ready", Product: product, Quote: ready) : Failure("conflict");
    }

    public async Task<NCMetaSoftwareOutcome> TryPurchaseAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid quoteId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty) return Failure("access");
        var gate = _commands[(uint) requestId.GetHashCode() % (uint) _commands.Length];
        if (!await gate.WaitAsync(0)) return Failure("busy");
        try
        {
            var input = PurchaseInput(context, quoteId);
            var remembered = await ReadAcknowledgement(context, requestId, "purchase", input);
            if (remembered.Found)
            {
                var receipt = remembered.Value;
                if (receipt == null || receipt.Quote == null || receipt.Command == null || receipt.Quote.Id != quoteId ||
                    receipt.Command.Fingerprint != PurchaseFingerprint(receipt.Quote)) return Failure("conflict");
                var previous = await Read<MetaSoftwareLicense>(LicenseType, receipt.Command.LicenseId.ToString("D"));
                return previous != null && previous.Owner == receipt.Quote.LicenseOwner && previous.ProductId == receipt.Quote.ProductId &&
                    previous.Major == receipt.Quote.Major && previous.PaymentTransactionId == receipt.Command.TransactionId &&
                    receipt.Command.LicenseId == NCMetaPersistenceSystem.StableGuid("software-license:" + quoteId)
                    ? new(true, "nc-meta-software-purchased", License: await PresentLicense(context, previous)) : Failure("conflict");
            }
            var quoteRead = await _persistence.ReadQuoteAsync("software", context.Actor, quoteId);
            var quote = quoteRead == null ? null : JsonSerializer.Deserialize<NCMetaSoftwareQuote>(quoteRead.Row.Payload, Json);
            if (quote != null && (!ValidSnapshot(quote, QuoteType, quoteRead!.Row.AggregateId, quoteRead.Row.AggregateVersion) || quote.Id != quoteId)) return Failure("conflict");
            if (quote == null || quote.SlotKind is not ("User" or "Firmware") || quote.Buyer != context.Actor) return Failure("access");
            var fingerprint = PurchaseFingerprint(quote);
            var command = NCPersistentIdentity.Command("meta", "software-purchase", context.Actor + ":" + requestId);
            var replay = await Read<NCMetaSoftwareCommand>(CommandType, command.ToString("D"));
            if (replay != null)
            {
                if (replay.Fingerprint != fingerprint) return Failure("conflict");
                var owned = await Read<MetaSoftwareLicense>(LicenseType, replay.LicenseId.ToString("D"));
                return owned != null && owned.Owner == quote.LicenseOwner ? new(true, "nc-meta-software-purchased", License: await PresentLicense(context, owned)) : Failure("conflict");
            }
            if (!await CanActFor(context, quote.LicenseOwner)) return Failure("access");
            var product = await ReadProductAsync(quote.ProductId, quote.Major);
            if (product is not { Offered: true } || quote.Consumed || product.Revision != quote.ProductRevision || MetaSoftwarePolicy.TermsFingerprint(product.Terms) != quote.TermsFingerprint || context.Time.Ticks >= quote.ExpiresAtTicks || !product.Releases.Any(release => release.Id == quote.ReleaseId && release.ArtifactDigest == quote.ArtifactDigest)) return Failure("expired");
            var selected = product.Releases.Single(release => release.Id == quote.ReleaseId);
            var compatible = await Task.WhenAll(quote.DeviceIds.Select(id => Compatibility(quote.LicenseOwner, id, selected, quote.SlotKind)));
            if (compatible.Any(value => value is not { Compatible: true })) return Failure("incompatible");
            var index = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, quote.LicenseOwner) ?? new(quote.LicenseOwner, [], 0);
            if (index.Licenses.Length >= 128) return Failure("quota");
            var licenseId = NCMetaPersistenceSystem.StableGuid("software-license:" + quote.Id);
            var transactionId = NCPersistentIdentity.Transaction("meta", "software-purchase", context.Actor + ":" + requestId);
            var license = new MetaSoftwareLicense(licenseId, product.Id, product.Major, quote.LicenseOwner, quote.Scope, quote.SeatCount,
                quote.DeviceIds.Select(device => new MetaSoftwareSeat(Guid.NewGuid(), device)).ToArray(), [], product.Terms,
                context.Time.Ticks, checked(context.Time.Ticks + product.Terms.SupportTicks), false, MetaLicenseProvenance.Purchase, transactionId.Value, null, null, 1)
                { BoundDeviceId = quote.Scope == MetaLicenseScope.Device ? quote.DeviceIds[0] : null };
            var writes = new List<NCAtomicLedgerParticipantWrite>
            {
                Write(LicenseType, licenseId.ToString("D"), 0, license),
                Write(OwnerType, quote.LicenseOwner, index.Revision, AddOwnerLicense(index, license)),
                Write(QuoteType, quoteRead!.Row.AggregateId, quote.Revision, quote with { Consumed = true, LicenseId = licenseId, Revision = quote.Revision + 1 }),
                Write(CommandType, command.ToString("D"), 0, new NCMetaSoftwareCommand(fingerprint, licenseId, transactionId.Value, 1))
            };
            var releaseTime = await Main(() => Current(context) && _persistence.TryCaptureTime(out _, out var time) ? (long?)time.Ticks : null);
            if (releaseTime == null) return Failure("access");
            var releaseSlot = _persistence.PrepareQuoteRelease("software", context.Actor, quote.Id, releaseTime.Value, quoteRead!);
            if (releaseSlot != null) writes.Add(new(releaseSlot.EventId, releaseSlot.EventType, releaseSlot.AggregateType, releaseSlot.AggregateId,
                releaseSlot.ExpectedAggregateVersion, releaseSlot.SnapshotFormatVersion, releaseSlot.SnapshotPayload, releaseSlot.EventPayload));
            foreach (var deviceId in quote.DeviceIds)
            {
                var authority = await ReadTargetAuthorityWrite(quote.LicenseOwner, deviceId);
                if (authority == null) return Failure("access");
                writes.Add(authority);
                var storage = await _persistence.PrepareSoftwareStorageAttestationAsync(deviceId, quote.LicenseOwner,
                    compatible.Single(value => value!.DeviceId == deviceId)!.StorageRevision);
                if (storage == null) return Failure("incompatible");
                writes.Add(new(storage.EventId, storage.EventType, storage.AggregateType, storage.AggregateId,
                    storage.ExpectedAggregateVersion, storage.SnapshotFormatVersion, storage.SnapshotPayload, storage.EventPayload));
                var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId) ?? new(deviceId, [], 0);
                var key = Key(product.Id, product.Major);
                if (device.Licenses.TryGetValue(key, out var priorId))
                {
                    var previous = await Read<MetaSoftwareLicense>(LicenseType, priorId.ToString("D"));
                    if (previous is { Revoked: false } && previous.Owner == quote.LicenseOwner && previous.Seats.Any(seat => seat.DeviceId == deviceId)) return Failure("already-owned");
                }
                else if (device.Licenses.Count >= 64) return Failure("quota");
                var entries = new Dictionary<string, Guid>(device.Licenses, StringComparer.Ordinal) { [key] = licenseId };
                writes.Add(Write(DeviceType, deviceId, device.Revision, new NCMetaSoftwareDeviceIndex(deviceId, entries, device.Revision + 1)));
            }
            if (!await CanActFor(context, quote.LicenseOwner) || !await Main(() => CurrentBefore(context, quote.ExpiresAtTicks) && quote.DeviceIds.All(id => _devices.TryResolve(id, out var device) && device.Owner == quote.LicenseOwner))) return Failure("access");
            bool success;
            var acknowledgement = Acknowledgement(context, "purchase", input, writes, quote);
            if (quote.Price == 0) success = await Batch(context, requestId, "purchase", input, writes, acknowledgement);
            else
            {
                if (!NCPartyRef.TryParse(quote.LicenseOwner, out var buyer) || !NCPartyRef.TryParse(product.Publisher, out var seller)) return Failure("access");
                var payer = await _finance.GetPrimaryAccountAsync(buyer, true); var payee = await _finance.GetPrimaryAccountAsync(seller, true);
                if (payer == null || payee == null || payer.Snapshot.Owner != buyer || payee.Snapshot.Owner != seller || payer.Snapshot.AccountId == payee.Snapshot.AccountId) return Failure("account");
                var prior = await _ledger.GetTransactionAsync(transactionId, true);
                if (prior != null && prior.Snapshot.Reference != "meta-software:" + fingerprint) return Failure("conflict");
                if (!await CanActFor(context, quote.LicenseOwner) || !await Main(() => CurrentBefore(context, quote.ExpiresAtTicks))) return Failure("access");
                var authority = await CaptureMutationAuthority(context);
                if (authority == null) return Failure("access");
                await BeforeAuthorityCommitAsync(requestId);
                var result = await _ledger.PostTransactionAsync(new(command, transactionId, NCLedgerTransactionKind.Purchase, "meta-software:" + fingerprint,
                    [new(payer.Snapshot.AccountId, payer.AggregateVersion, -quote.Price), new(payee.Snapshot.AccountId, payee.AggregateVersion, quote.Price)], buyer, requestId, AuditSource: "meta.software",
                    ParticipantWrites: writes.Where(write => write.AggregateType != CommandType).ToArray(), Authority: authority, ParticipantResultPayload: acknowledgement));
                success = result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate && result.Transaction?.Snapshot.Reference == "meta-software:" + fingerprint;
                if (result.Status == NCEconomyMutationStatus.InsufficientFunds) return Failure("funds");
            }
            if (!success) return Failure("conflict");
            var committed = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
            return committed != null && committed.PaymentTransactionId == transactionId.Value ? new(true, "nc-meta-software-purchased", Product: product, License: await PresentLicense(context, committed)) : Failure("conflict");
        }
        finally { gate.Release(); }
    }
    private static string PurchaseFingerprint(NCMetaSoftwareQuote quote)
    {
        var compatibility = JsonSerializer.Serialize(quote.Compatibility);
        if (!quote.SlotAware)
        {
            // Existing receipts predate SlotKind. Preserve their exact serialized compatibility shape.
            using var document = JsonDocument.Parse(compatibility);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (var property in row.EnumerateObject())
                        if (property.Name != nameof(MetaSoftwareCompatibility.SlotKind)) property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            compatibility = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        string[] parts = [quote.Id.ToString("D"), quote.Buyer, quote.LicenseOwner,
            quote.ProductId.ToString("D"), quote.Major.ToString(CultureInfo.InvariantCulture), quote.ReleaseId.ToString("D"), quote.ArtifactDigest,
            ((int) quote.Scope).ToString(CultureInfo.InvariantCulture), quote.SeatCount.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(quote.DeviceIds),
            quote.Price.ToString(CultureInfo.InvariantCulture), quote.TermsFingerprint, quote.ProductRevision.ToString(CultureInfo.InvariantCulture),
            quote.ExpiresAtTicks.ToString(CultureInfo.InvariantCulture), compatibility];
        return MetaSoftwarePolicy.Fingerprint(quote.SlotAware ? [..parts, quote.SlotKind] : parts);
    }
}
