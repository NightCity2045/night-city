// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using Content.Meta.Commerce;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    public async Task<NCMetaSoftwareOutcome> TryOfferResaleAsync(EntityUid terminal, EntityUid user, Guid requestId,
        Guid licenseId, string buyer, long price)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || price is < 0 or > 1000000 ||
            !NCPartyRef.TryParse(buyer, out var buyerParty) || !(await _softwareParties.ResolveAsync(buyerParty, true)).IsResolved) return Failure("access");
        var license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
        if (license == null) return Failure("access");
        var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseId.ToString("D"), buyer,
            price.ToString(CultureInfo.InvariantCulture));
        var replay = await ReadCommand(context, requestId, "resale-offer", fingerprint, licenseId);
        if (replay.Conflict) return Failure("conflict");
        var old = await Read<NCMetaSoftwareResaleOffer>("meta.software.resale", licenseId.ToString("D"));
        if (replay.Found)
        {
            var previous = replay.Receipt?.ResaleOffer ?? old;
            return previous != null && previous.Buyer == buyer && previous.Price == price
                ? new(true, "nc-meta-software-resale-ready", License: await PresentLicense(context, license), Resale: previous) : Failure("conflict");
        }
        if (buyer == license.Owner || !MetaSoftwarePolicy.CanResell(license) || !await CanActFor(context, license.Owner)) return Failure("access");
        var offer = new NCMetaSoftwareResaleOffer(licenseId, license.Owner, buyer, license.Revision, price,
            checked(context.Time.Ticks + TimeSpan.FromMinutes(2).Ticks), false, Guid.Empty, (old?.Revision ?? 0) + 1);
        var recipient = await Read<NCMetaSoftwareResaleRecipientIndex>(ResaleRecipientsType, buyer) ?? new(buyer, [], 0);
        var live = (await ReadActiveRecipientOffers(recipient, context.Time.Ticks)).Select(value => value.LicenseId).Where(id => id != licenseId).ToArray();
        if (live.Length >= 128) return Failure("quota");
        var writes = new List<NCAtomicLedgerParticipantWrite>
        { Write("meta.software.resale", licenseId.ToString("D"), old?.Revision ?? 0, offer),
            Write(ResaleRecipientsType, buyer, recipient.Revision, recipient with { Licenses = [.. live, licenseId], Revision = checked(recipient.Revision + 1) }),
            CommandWrite(context, requestId, "resale-offer", fingerprint, licenseId, offer: offer) };
        if (old != null && old.Buyer != buyer)
        {
            var former = await Read<NCMetaSoftwareResaleRecipientIndex>(ResaleRecipientsType, old.Buyer);
            if (former != null) writes.Add(Write(ResaleRecipientsType, former.Owner, former.Revision, former with
                { Licenses = former.Licenses.Where(id => id != licenseId).ToArray(), Revision = checked(former.Revision + 1) }));
        }
        return await Batch(context, requestId, "resale-offer", fingerprint, writes)
            ? new(true, "nc-meta-software-resale-ready", License: await PresentLicense(context, license), Resale: offer) : Failure("conflict");
    }

    public async Task<NCMetaSoftwareOutcome> TryAcceptResaleAsync(EntityUid terminal, EntityUid user, Guid requestId,
        Guid licenseId, long exactOfferRevision)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var context = await Main(() => Capture(terminal, user));
        if (mutation == null || context == null || requestId == Guid.Empty || exactOfferRevision < 1) return Failure("access");
        var gate = _commands[(uint) requestId.GetHashCode() % (uint) _commands.Length];
        if (!await gate.WaitAsync(0)) return Failure("busy");
        try
        {
            var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, licenseId.ToString("D"), exactOfferRevision.ToString(CultureInfo.InvariantCulture));
            var replay = await ReadCommand(context, requestId, "resale", fingerprint, licenseId);
            if (replay.Conflict) return Failure("conflict");
            var license = await Read<MetaSoftwareLicense>(LicenseType, licenseId.ToString("D"));
            if (license == null) return Failure("access");
            if (replay.Found)
                return new(true, "nc-meta-software-transferred", License: await PresentLicense(context, license));
            var offer = await Read<NCMetaSoftwareResaleOffer>("meta.software.resale", licenseId.ToString("D"));
            if (offer == null || offer.Revision != exactOfferRevision || offer.Consumed || context.Time.Ticks >= offer.ExpiresAtTicks ||
                offer.Seller != license.Owner || offer.LicenseRevision != license.Revision || !MetaSoftwarePolicy.CanResell(license) ||
                !await CanActFor(context, offer.Buyer)) return Failure("expired");
            var oldIndex = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, offer.Seller);
            var newIndex = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, offer.Buyer) ?? new(offer.Buyer, [], 0);
            if (oldIndex == null || !oldIndex.Licenses.Contains(licenseId) || newIndex.Licenses.Length >= 128 ||
                newIndex.Licenses.Contains(licenseId)) return Failure("quota");
            var command = NCPersistentIdentity.Command("meta", "software-resale", context.Actor + ":" + requestId);
            var transaction = NCPersistentIdentity.Transaction("meta", "software-resale", context.Actor + ":" + requestId);
            var updated = license with { Owner = offer.Buyer, Seats = [], Activations = [], Provenance = MetaLicenseProvenance.Transfer,
            PaymentTransactionId = transaction.Value, BoundDeviceId = null, Revision = license.Revision + 1 };
            var recipient = await Read<NCMetaSoftwareResaleRecipientIndex>(ResaleRecipientsType, offer.Buyer);
            if (recipient == null || !recipient.Licenses.Contains(licenseId)) return Failure("access");
            var writes = new[]
            {
                Write(LicenseType, licenseId.ToString("D"), license.Revision, updated),
                Write(OwnerType, offer.Seller, oldIndex.Revision, RemoveOwnerLicense(oldIndex, license)),
                Write(OwnerType, offer.Buyer, newIndex.Revision, AddOwnerLicense(newIndex, updated)),
                Write("meta.software.resale", licenseId.ToString("D"), offer.Revision, offer with { Consumed = true, TransactionId = transaction.Value, Revision = offer.Revision + 1 }),
                Write(ResaleRecipientsType, offer.Buyer, recipient.Revision, recipient with
                    { Licenses = recipient.Licenses.Where(id => id != licenseId).ToArray(), Revision = checked(recipient.Revision + 1) }),
                CommandWrite(context, requestId, "resale", fingerprint, licenseId, transaction.Value)
            };
            if (!await CanActFor(context, offer.Buyer) || !await Main(() => CurrentBefore(context, offer.ExpiresAtTicks))) return Failure("access");
            bool applied;
            if (offer.Price == 0) applied = await Batch(context, requestId, "resale", fingerprint, writes);
            else
            {
                if (!NCPartyRef.TryParse(offer.Buyer, out var buyer) || !NCPartyRef.TryParse(offer.Seller, out var seller)) return Failure("access");
                var payer = await _finance.GetPrimaryAccountAsync(buyer, true);
                var payee = await _finance.GetPrimaryAccountAsync(seller, true);
                if (payer == null || payee == null || payer.Snapshot.Owner != buyer || payee.Snapshot.Owner != seller || payer.Snapshot.AccountId == payee.Snapshot.AccountId) return Failure("account");
                if (!await CanActFor(context, offer.Buyer) || !await Main(() => CurrentBefore(context, offer.ExpiresAtTicks))) return Failure("access");
                var authority = await CaptureMutationAuthority(context);
                if (authority == null) return Failure("access");
                await BeforeAuthorityCommitAsync(requestId);
                var result = await _ledger.PostTransactionAsync(new(command, transaction, NCLedgerTransactionKind.Purchase,
                    "meta-resale:" + fingerprint,
                    [new(payer.Snapshot.AccountId, payer.AggregateVersion, -offer.Price), new(payee.Snapshot.AccountId, payee.AggregateVersion, offer.Price)],
                    buyer, requestId, AuditSource: "meta.software.resale", ParticipantWrites: writes.Where(write => write.AggregateType != CommandType).ToArray(), Authority: authority,
                    ParticipantResultPayload: Acknowledgement(context, "resale", fingerprint, writes)));
                if (result.Status == NCEconomyMutationStatus.InsufficientFunds) return Failure("funds");
                applied = result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate &&
                    result.Transaction?.Snapshot.Reference == "meta-resale:" + fingerprint;
            }
            if (!applied) return Failure("conflict");
            await Main(() => { foreach (var seat in license.Seats) RevokeDevice(seat.DeviceId); EntitlementChanged?.Invoke(license.ProductId, license.Major, license.Owner); return true; });
            return new(true, "nc-meta-software-transferred", License: await PresentLicense(context, updated));
        }
        finally { gate.Release(); }
    }

}
