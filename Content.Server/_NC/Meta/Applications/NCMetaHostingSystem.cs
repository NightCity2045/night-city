// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Mui;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Applications;

/// <summary>Prepaid CitiNet names and hosting quotas. Money and every entitlement commit as one ledger settlement.</summary>
public sealed partial class NCMetaHostingSystem : EntitySystem
{
    private const string OfferType = "meta.hosting-offer";
    private const string QuoteType = "meta.hosting-quote";
    private const string LeaseType = "meta.hosting-lease";
    private const string DomainType = "meta.hosting-domain";
    private const string CapacityType = "meta.hosting-capacity";
    private const string OwnerType = "meta.hosting-owner";
    private static readonly ProtoId<NCMetaHostingProfilePrototype> ProfileId = "NCMetaHostingStandard";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private sealed record Context(EntityUid Terminal, EntityUid User, EntityUid Host, string Buyer, string Provider, string HostId,
        ulong TerminalEpoch, ulong TerminalAuthority, ulong HostEpoch, ulong HostAuthority, Guid Generation,
        string PackageDigest, NCSessionEpoch WorldEpoch, NCActiveWorldTime Time, NCMetaManagementProof TerminalManagement);
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaApplicationSystem _applications = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private readonly Dictionary<Guid, NCMetaHostingLease> _active = [];
    private readonly Dictionary<Guid, long> _versions = [];
    private readonly Queue<Guid> _versionOrder = [];
    private readonly Queue<Guid> _audit = [];
    private readonly Dictionary<string, NCMetaHostingLease> _domains = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _defaultSince = [];
    private readonly HashSet<Guid> _settling = [];
    private bool _loading;
    private bool _loaded;
    private string? _cursor;
    private long _nextAudit;
    private NCSessionEpoch _worldEpoch;
    private ulong _revocations;
    private readonly HashSet<Guid> _revoking = [];

    private Context? Capture(EntityUid terminal, EntityUid user, string hostId, Guid runtimeGeneration = default)
    {
        if (!_ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) || !_devices.CanManage(terminal, user) ||
            !_devices.TryGetEntry(terminal, out var source) || !_devices.TryResolve(hostId, out var host) ||
            !_parties.TryResolve(user, out var buyer) || !(runtimeGeneration == Guid.Empty
                ? _applications.TryGetRegistration(host.Entity, out var app) : _applications.TryGetRegistration(host.Entity, runtimeGeneration, out app)) ||
            !_runtime.CanReach(terminal, host.Entity) || !_authority.TryGetManagementProof(user, source.Owner, out var management) ||
            !_persistence.TryCaptureTime(out var epoch, out var time))
            return null;
        return new(terminal, user, host.Entity, buyer.ToString(), app.Owner, hostId, source.DeviceEpoch, source.AuthorityEpoch,
            host.DeviceEpoch, host.AuthorityEpoch, app.RuntimeGeneration, app.Application.PackageDigest, epoch, time, management);
    }

    private bool Current(Context context) => _ui.IsUiOpen(context.Terminal, NCMetaUiKey.Workspace, context.User) &&
        _devices.CanManage(context.Terminal, context.User) && _authority.IsCurrent(context.TerminalManagement) && _parties.TryResolve(context.User, out var buyer) && buyer.ToString() == context.Buyer &&
        _devices.TryGetEntry(context.Terminal, out var terminal) && terminal.DeviceEpoch == context.TerminalEpoch && terminal.AuthorityEpoch == context.TerminalAuthority &&
        _devices.TryResolve(context.HostId, out var host) && host.Entity == context.Host && host.Owner == context.Provider &&
        host.DeviceEpoch == context.HostEpoch && host.AuthorityEpoch == context.HostAuthority && _runtime.CanReach(context.Terminal, context.Host) &&
        _applications.TryGetRegistration(context.Host, context.Generation, out var app) &&
        app.Application.PackageDigest == context.PackageDigest && _persistence.TryCaptureTime(out var epoch, out _) && epoch == context.WorldEpoch;

    private Context? CapturePackage(EntityUid terminal, EntityUid user, string hostId, string packageDigest) =>
        _devices.TryResolve(hostId, out var host) && _applications.TryGetRegistration(host.Entity, packageDigest, out var app)
            ? Capture(terminal, user, hostId, app.RuntimeGeneration) : null;

    public async Task<NCMetaHostingOutcome> TryOfferAsync(EntityUid host, EntityUid user, Guid requestId, string view, Guid runtimeGeneration = default)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return Failure("nc-meta-error-busy");
        if (requestId == Guid.Empty || !_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device) ||
            !_authority.TryGetManagementProof(user, device.Owner, out var management) ||
            !(runtimeGeneration == Guid.Empty ? _applications.TryGetRegistration(host, out var registration) :
                _applications.TryGetRegistration(host, runtimeGeneration, out registration)) || !registration.Application.Views.ContainsKey(view) ||
            !TryComp<NCMetaPortComponent>(host, out var port) || port.NetworkId != "CITYNET" ||
            !_persistence.TryCaptureTime(out var epoch, out var time))
            return Failure("nc-meta-hosting-access");
        var owner = device.Owner;
        var hostId = device.Id;
        var generation = registration.RuntimeGeneration;
        var digest = registration.Application.PackageDigest;
        var current = await Read<NCMetaHostingOffer>(OfferType, hostId);
        if (current is not null && current.Provider != owner)
            return Failure("nc-meta-hosting-conflict");
        var next = new NCMetaHostingOffer(hostId, owner, digest, view, ProfileId.Id, true, (current?.Revision ?? 0) + 1);
        var hash = Fingerprint(owner, hostId, view, digest);
        var authority = await Main(() => _authority.IsCurrent(management) && _devices.CanManage(host, user) &&
            _devices.TryGetEntry(host, out var currentDevice) && currentDevice.Id == hostId && currentDevice.Owner == owner &&
            currentDevice.DeviceEpoch == device.DeviceEpoch && currentDevice.AuthorityEpoch == device.AuthorityEpoch &&
            TryComp<NCMetaPortComponent>(host, out var currentPort) && currentPort.NetworkId == "CITYNET" &&
            _applications.TryGetRegistration(host, generation, out var app) &&
            _persistence.TryCaptureTime(out var freshEpoch, out _) && freshEpoch == epoch ? _authority.CaptureWriteConstraints(management, host) : null);
        if (authority == null)
            return Failure("nc-meta-hosting-access");
        var persisted = await _database.ApplyNCPersistentMutationAsync(new(requestId, "meta.hosting.offer", "meta.owner", owner,
            Guid.NewGuid(), "meta.hosting.offered", OfferType, hostId, current?.Revision ?? 0, 1,
            JsonSerializer.Serialize(next, Json), "{}", "{}", requestId, null, epoch, time, [], RequestFingerprint: hash, Authority: authority));
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return Failure("nc-meta-hosting-conflict");
        var committed = await Read<NCMetaHostingOffer>(OfferType, hostId);
        if (committed is null || !committed.Active || committed.Provider != owner || committed.PackageDigest != digest || committed.View != view)
            return Failure("nc-meta-hosting-conflict");
        var published = await Main(() => _applications.TryPublishView(host, user, view, generation));
        return new(published, published ? "nc-meta-hosting-offered" : "nc-meta-hosting-access");
    }

    public async Task<NCMetaHostingOutcome> TryQuoteAsync(EntityUid terminal, EntityUid user, Guid requestId,
        string domain, string hostId, string view, int units, Guid runtimeGeneration = default)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return Failure("nc-meta-error-busy");
        var context = Capture(terminal, user, hostId, runtimeGeneration);
        if (context is null || requestId == Guid.Empty || units is < 0 or > 16 || !MuiCitiNetAddress.IsDomain(domain) ||
            !MuiBinding.IsPath(view) || !_prototypes.TryIndex(ProfileId, out var profile) || !ValidProfile(profile))
            return Failure("nc-meta-hosting-access");
        if (!_applications.TryGetRegistration(context.Host, context.Generation, out var selected) || !selected.Application.Views.ContainsKey(view))
            return Failure("nc-meta-hosting-unavailable");
        var quoteId = NCMetaPersistenceSystem.StableGuid("hosting-quote:" + context.Buyer + ":" + requestId);
        var fingerprint = Fingerprint(context.Buyer, domain, hostId, view, units.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var remembered = await ReadQuoteAcknowledgement(requestId, context.Buyer, fingerprint);
        if (remembered.Found) return remembered.Quote is { } priorQuote && await Main(() => Current(context)) ? new(true, "nc-meta-hosting-quote-ready", Summary(priorQuote)) : Failure("nc-meta-hosting-conflict");
        var existing = await Read<NCMetaHostingQuote>(QuoteType, quoteId.ToString("D"));
        if (existing is not null)
            return existing.Buyer == context.Buyer && existing.Domain == domain && existing.HostDeviceId == hostId && existing.View == view && existing.Units == units
                ? new(true, "nc-meta-hosting-quote-ready", Summary(existing)) : Failure("nc-meta-hosting-conflict");
        var offer = await Read<NCMetaHostingOffer>(OfferType, hostId);
        if (units == 0 ? context.Provider != context.Buyer : offer is null || !offer.Active || offer.Provider != context.Provider ||
            offer.View != view || offer.PackageDigest != context.PackageDigest)
            return Failure("nc-meta-hosting-unavailable");
        var pointer = await Read<NCMetaHostingDomain>(DomainType, domain);
        if (pointer is not null && pointer.ExpiresAtTicks > context.Time.Ticks)
            return Failure("nc-meta-hosting-domain-taken");
        var quoteTime = await Main(() => Current(context) && _persistence.TryCaptureTime(out _, out var time) ? (long?)time.Ticks : null);
        if (quoteTime == null) return Failure("nc-meta-hosting-access");
        var expires = checked(quoteTime.Value + TimeSpan.FromSeconds(profile.QuoteSeconds).Ticks);
        var slot = await _persistence.PrepareQuoteAsync("hosting", context.Buyer, quoteId, quoteTime.Value, expires);
        if (slot == null) return Failure("nc-meta-hosting-capacity");
        var quote = new NCMetaHostingQuote(quoteId, context.Buyer, context.Provider, domain, hostId, context.PackageDigest,
            view, units, profile.DomainFee, checked(profile.HostingUnitFee * units), TimeSpan.FromSeconds(profile.TermSeconds).Ticks,
            units == 0 ? MuiLimits.InstallationSessions : units * profile.SessionsPerUnit, expires,
            false, Guid.Empty, offer?.Revision ?? 0, slot.PreviousSlotRevision + 1) { PoolSlot = slot.Slot };
        if (!await Main(() => Current(context)))
            return Failure("nc-meta-hosting-access");
        var authority = await CaptureHostingAuthority(context);
        if (authority == null) return Failure("nc-meta-hosting-access");
        var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.hosting.quote", "meta.owner", context.Buyer,
            [slot.PoolWrite, new(Guid.NewGuid(), "meta.hosting.quoted", QuoteType, slot.StorageKey, slot.PreviousSlotRevision, 1, JsonSerializer.Serialize(quote, Json), "{}")],
            JsonSerializer.Serialize(new HostingQuoteAcknowledgement(1, quote), Json), requestId, null, context.WorldEpoch, context.Time, [], RequestFingerprint: fingerprint, Authority: authority));
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)) return Failure("nc-meta-hosting-conflict");
        var committed = await ReadQuoteAcknowledgement(requestId, context.Buyer, fingerprint);
        return committed.Quote is { } ready ? new(true, "nc-meta-hosting-quote-ready", Summary(ready)) : Failure("nc-meta-hosting-conflict");
    }

    public async Task<NCMetaHostingOutcome> TryPurchaseAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid quoteId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return Failure("nc-meta-error-busy");
        if (requestId == Guid.Empty || quoteId == Guid.Empty || !_parties.TryResolve(user, out var buyer))
            return Failure("nc-meta-hosting-access");
        var buyerKey = buyer.ToString();
        var remembered = await ReadPurchaseAcknowledgement(terminal, user, requestId, buyerKey, quoteId);
        if (remembered.Found) return remembered.Lease is { } priorLease ? new(true, "nc-meta-hosting-purchased", Lease: Summary(priorLease)) : Failure("nc-meta-hosting-conflict");
        var quoteRead = await _persistence.ReadQuoteAsync("hosting", buyerKey, quoteId);
        var quote = quoteRead == null ? null : JsonSerializer.Deserialize<NCMetaHostingQuote>(quoteRead.Row.Payload, Json);
        if (quote != null && (quote.Id != quoteId || !Valid(quote, quoteRead!.Row.AggregateId, quoteRead.Row.AggregateVersion))) return Failure("nc-meta-hosting-conflict");
        if (quote is null || quote.Buyer != buyerKey)
            return Failure("nc-meta-hosting-access");
        var fingerprint = Fingerprint(buyerKey, quoteId.ToString("D"));
        var transactionId = NCPersistentIdentity.Transaction("meta", "hosting-purchase", buyerKey + ":" + requestId);
        var replay = await _ledger.GetTransactionAsync(transactionId, true);
        if (replay is not null)
        {
            if (replay.Snapshot.Reference != "meta-hosting:" + fingerprint || !quote.Consumed)
                return Failure("nc-meta-hosting-conflict");
            var leased = await Read<NCMetaHostingLease>(LeaseType, quote.LeaseId.ToString("D"));
            return leased is not null && leased.PaymentTransactionId == transactionId.Value
                ? new(true, "nc-meta-hosting-purchased", Lease: Summary(leased)) : Failure("nc-meta-hosting-conflict");
        }
        var context = await Main(() => CapturePackage(terminal, user, quote.HostDeviceId, quote.PackageDigest));
        var profile = await Main(() => _prototypes.TryIndex(ProfileId, out var result) && ValidProfile(result) ? result : null);
        if (context is null || context.Buyer != quote.Buyer || context.Provider != quote.Provider || context.PackageDigest != quote.PackageDigest ||
            quote.Consumed || context.Time.Ticks >= quote.ExpiresAtTicks || profile is null)
            return Failure("nc-meta-hosting-expired");
        var offer = await Read<NCMetaHostingOffer>(OfferType, quote.HostDeviceId);
        if (quote.Units > 0 && (offer is null || !offer.Active || offer.Revision != quote.OfferRevision || offer.Provider != quote.Provider ||
            offer.PackageDigest != quote.PackageDigest || offer.View != quote.View))
            return Failure("nc-meta-hosting-unavailable");
        var domain = await Read<NCMetaHostingDomain>(DomainType, quote.Domain);
        if (domain is not null && domain.ExpiresAtTicks > context.Time.Ticks)
            return Failure("nc-meta-hosting-domain-taken");
        var capacity = await Read<NCMetaHostingCapacity>(CapacityType, quote.HostDeviceId) ?? new(quote.HostDeviceId, [], 0);
        var reservations = new Dictionary<Guid, int>();
        foreach (var reservation in capacity.Reservations.Take(64))
        {
            var active = await Read<NCMetaHostingLease>(LeaseType, reservation.Key.ToString("D"));
            if (active is { Status: NCMetaHostingLeaseStatus.Active } && active.ExpiresAtTicks > context.Time.Ticks)
                reservations.Add(reservation.Key, reservation.Value);
        }
        if (reservations.Values.Sum() + quote.Units > profile.CapacityUnits)
            return Failure("nc-meta-hosting-capacity");
        var ownerIndex = await Read<NCMetaHostingOwnerIndex>(OwnerType, buyerKey) ?? new(buyerKey, [], 0);
        var ownedLeases = new List<Guid>();
        foreach (var previousId in ownerIndex.Leases)
        {
            var previousLease = await Read<NCMetaHostingLease>(LeaseType, previousId.ToString("D"));
            if (previousLease is { Status: NCMetaHostingLeaseStatus.Active } && previousLease.Buyer == buyerKey)
                ownedLeases.Add(previousId);
        }
        if (ownedLeases.Count >= 64 || !await Main(() => _active.Count < 1024))
            return Failure("nc-meta-hosting-capacity");
        var registry = await ServiceAccount("citinet-registry", "nc-meta-hosting-registry");
        var escrow = await ServiceAccount("citinet-hosting-escrow", "nc-meta-hosting-escrow");
        var payer = await _finance.GetPrimaryAccountAsync(buyer, true);
        if (registry is null || escrow is null || payer is null || payer.Snapshot.Owner != buyer)
            return Failure("nc-meta-hosting-account");
        var refreshed = await Main(() => Current(context) ? Capture(terminal, user, quote.HostDeviceId, context.Generation) : null);
        if (refreshed is null || refreshed.Time.Ticks >= quote.ExpiresAtTicks)
            return Failure("nc-meta-hosting-expired");
        context = refreshed;
        var leaseId = NCMetaPersistenceSystem.StableGuid("hosting-lease:" + quote.Id);
        ownedLeases.Add(leaseId);
        var lease = new NCMetaHostingLease(leaseId, quote.Id, quote.Buyer, quote.Provider, quote.Domain, quote.HostDeviceId,
            quote.PackageDigest, quote.View, quote.Units, quote.MaxSessions, context.Time.Ticks, checked(context.Time.Ticks + quote.TermTicks),
            quote.DomainFee, quote.HostingFee, transactionId.Value, NCMetaHostingLeaseStatus.Active, null, 1);
        if (quote.Units > 0)
            reservations.Add(leaseId, quote.Units);
        var nextCapacity = capacity with { Reservations = reservations, Revision = capacity.Revision + 1 };
        var nextDomain = new NCMetaHostingDomain(quote.Domain, leaseId, quote.Buyer, lease.ExpiresAtTicks, (domain?.Revision ?? 0) + 1);
        var legs = new List<NCLedgerLegRequest>
        {
            new(payer.Snapshot.AccountId, payer.AggregateVersion, -checked(quote.DomainFee + quote.HostingFee)),
            new(registry.Snapshot.AccountId, registry.AggregateVersion, quote.DomainFee),
        };
        if (quote.HostingFee > 0)
            legs.Add(new(escrow.Snapshot.AccountId, escrow.AggregateVersion, quote.HostingFee));
        if (!await Main(() => Current(context)))
            return Failure("nc-meta-hosting-access");
        var participants = new List<NCAtomicLedgerParticipantWrite>
        {Write(QuoteType, quoteRead!.Row.AggregateId, quote.Revision, quote with { Consumed = true, LeaseId = leaseId, Revision = quote.Revision + 1 }),
            Write(LeaseType, leaseId.ToString("D"), 0, lease), Write(DomainType, quote.Domain, domain?.Revision ?? 0, nextDomain),
            Write(CapacityType, quote.HostDeviceId, capacity.Revision, nextCapacity),
            Write(OwnerType, buyerKey, ownerIndex.Revision, new NCMetaHostingOwnerIndex(buyerKey, ownedLeases.ToArray(), ownerIndex.Revision + 1))};
        var releaseSlot = _persistence.PrepareQuoteRelease("hosting", buyerKey, quote.Id, context.Time.Ticks, quoteRead!);
        if (releaseSlot != null) participants.Add(new(releaseSlot.EventId, releaseSlot.EventType, releaseSlot.AggregateType, releaseSlot.AggregateId,
            releaseSlot.ExpectedAggregateVersion, releaseSlot.SnapshotFormatVersion, releaseSlot.SnapshotPayload, releaseSlot.EventPayload));
        var authority = await CaptureHostingAuthority(context);
        if (authority == null) return Failure("nc-meta-hosting-access");
        var result = await _ledger.PostTransactionAsync(new(requestId, transactionId, NCLedgerTransactionKind.Rent,
            "meta-hosting:" + fingerprint, legs.ToArray(), buyer, requestId, AuditSource: "meta.hosting",
            ParticipantWrites: participants, Authority: authority, ParticipantResultPayload: JsonSerializer.Serialize(new HostingPurchaseAcknowledgement(1, buyerKey, quote.Id, leaseId, transactionId.Value), Json)));
        if (result.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            return Failure(result.Status == NCEconomyMutationStatus.InsufficientFunds ? "nc-meta-hosting-funds" : "nc-meta-hosting-conflict");
        var committed = await Read<NCMetaHostingLease>(LeaseType, leaseId.ToString("D"));
        if (committed is null || committed.PaymentTransactionId != transactionId.Value || committed.Buyer != buyerKey)
            return Failure("nc-meta-hosting-conflict");
        await Main(() => { if (CurrentEpoch(context.WorldEpoch)) Cache(committed); return true; });
        return new(true, "nc-meta-hosting-purchased", Lease: Summary(committed));
    }

    public async Task<NCMetaHostingOutcome> TryRefundAsync(EntityUid terminal, EntityUid user, Guid requestId, Guid leaseId)
    {
        if (!_devices.CanManage(terminal, user) || !_ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) || !_parties.TryResolve(user, out var buyer) ||
            !_devices.TryGetEntry(terminal, out var device) || !_authority.TryGetManagementProof(user, device.Owner, out var management) ||
            !_persistence.TryCaptureTime(out var epoch, out _))
            return Failure("nc-meta-hosting-access");
        var deviceEpoch = device.DeviceEpoch;
        var authorityEpoch = device.AuthorityEpoch;
        var lease = await Read<NCMetaHostingLease>(LeaseType, leaseId.ToString("D"));
        if (lease is null || lease.Buyer != buyer.ToString())
            return Failure("nc-meta-hosting-access");
        var authority = await Main(() => _authority.CaptureWriteConstraints(management, terminal));
        if (authority == null) return Failure("nc-meta-hosting-access");
        return await SettleAsync(lease, requestId, NCMetaHostingLeaseStatus.Refunded, () =>
            _authority.IsCurrent(management) && _devices.CanManage(terminal, user) && _ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) &&
            _parties.TryResolve(user, out var currentBuyer) && currentBuyer == buyer &&
            _devices.TryGetEntry(terminal, out var current) && current.DeviceEpoch == deviceEpoch && current.AuthorityEpoch == authorityEpoch &&
            _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == epoch, authority);
    }

    private async Task<NCMetaHostingOutcome> SettleAsync(NCMetaHostingLease lease, Guid requestId, NCMetaHostingLeaseStatus status, Func<bool>? authorized = null,
        NCMetaAuthorityConstraints? authority = null)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return Failure("nc-meta-error-busy");
        var epoch = await Main(() => EnsureEpoch() && _revoking.Add(lease.Id) ? _worldEpoch : default);
        if (!epoch.IsValid) return Failure("nc-meta-error-busy");
        try { return await SettleCoreAsync(lease, requestId, status, authorized, authority); }
        finally { await Main(() => { if (_worldEpoch == epoch) _revoking.Remove(lease.Id); return true; }); }
    }

    private async Task<NCMetaHostingOutcome> SettleCoreAsync(NCMetaHostingLease lease, Guid requestId, NCMetaHostingLeaseStatus status, Func<bool>? authorized,
        NCMetaAuthorityConstraints? authority)
    {
        var stamp = await Main(() => _persistence.TryCaptureTime(out var epoch, out var time) ? (epoch, time) : (default, default));
        if (!stamp.epoch.IsValid || requestId == Guid.Empty || !NCPartyRef.TryParse(lease.Buyer, out var buyer) || !NCPartyRef.TryParse(lease.Provider, out var provider))
            return Failure("nc-meta-hosting-access");
        if (lease.Status != NCMetaHostingLeaseStatus.Active)
            return new(true, "nc-meta-hosting-settled", Lease: Summary(lease));
        var key = lease.Buyer + ":" + requestId;
        var transactionId = NCPersistentIdentity.Transaction("meta", "hosting-settle", key);
        var fingerprint = Fingerprint(lease.Buyer, lease.Id.ToString("D"), ((byte) status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var replay = await _ledger.GetTransactionAsync(transactionId, true);
        if (replay is not null && replay.Snapshot.Reference != "meta-hosting:" + fingerprint)
            return Failure("nc-meta-hosting-conflict");
        var capacity = await Read<NCMetaHostingCapacity>(CapacityType, lease.HostDeviceId) ?? new(lease.HostDeviceId, [], 0);
        var reservations = new Dictionary<Guid, int>(capacity.Reservations);
        reservations.Remove(lease.Id);
        var domain = await Read<NCMetaHostingDomain>(DomainType, lease.Domain);
        var next = lease with { Status = status, SettlementTransactionId = transactionId.Value, Revision = lease.Revision + 1 };
        var writes = new List<NCAtomicLedgerParticipantWrite>
        {
            Write(LeaseType, lease.Id.ToString("D"), lease.Revision, next),
            Write(CapacityType, lease.HostDeviceId, capacity.Revision, capacity with { Reservations = reservations, Revision = capacity.Revision + 1 }),
        };
        if (domain is not null && domain.LeaseId == lease.Id)
            writes.Add(Write(DomainType, lease.Domain, domain.Revision, domain with { ExpiresAtTicks = stamp.time.Ticks, Revision = domain.Revision + 1 }));
        if (!await Main(() => (authorized?.Invoke() ?? true) && _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == stamp.epoch))
            return Failure("nc-meta-hosting-access");
        var success = false;
        if (lease.HostingFee == 0)
        {
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(requestId, "meta.hosting.settle", "meta.owner", lease.Buyer,
                writes.Select(x => new NCPersistentAggregateWrite(x.EventId, x.EventType, x.AggregateType, x.AggregateId,
                    x.ExpectedAggregateVersion, x.SnapshotFormatVersion, x.SnapshotPayload, x.EventPayload)).ToArray(),
                "{}", requestId, null, stamp.epoch, stamp.time, [], RequestFingerprint: fingerprint, Authority: authority));
            success = result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
        }
        else
        {
            var escrow = await ServiceAccount("citinet-hosting-escrow", "nc-meta-hosting-escrow");
            var registry = await ServiceAccount("citinet-registry", "nc-meta-hosting-registry");
            var subscriber = await _finance.GetPrimaryAccountAsync(buyer, true);
            var reseller = await _finance.GetPrimaryAccountAsync(provider, true);
            if (escrow is null || registry is null || subscriber is null || reseller is null)
                return Failure("nc-meta-hosting-account");
            if (!await Main(() => (authorized?.Invoke() ?? true) && _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == stamp.epoch))
                return Failure("nc-meta-hosting-access");
            var elapsed = Math.Clamp(stamp.time.Ticks - lease.StartsAtTicks, 0, lease.ExpiresAtTicks - lease.StartsAtTicks);
            var earned = status == NCMetaHostingLeaseStatus.ProviderDefault ? 0 :
                (long) ((decimal) lease.HostingFee * elapsed / Math.Max(1, lease.ExpiresAtTicks - lease.StartsAtTicks));
            var refundable = lease.HostingFee - earned;
            var amounts = new Dictionary<NCAccountId, (long Version, long Amount)>();
            Add(escrow, -lease.HostingFee);
            if (earned > 0) Add(reseller, earned);
            if (refundable > 0) Add(subscriber, refundable);
            var legs = amounts.Where(x => x.Value.Amount != 0).Select(x => new NCLedgerLegRequest(x.Key, x.Value.Version, x.Value.Amount)).ToArray();
            var result = await _ledger.PostTransactionAsync(new(requestId, transactionId, NCLedgerTransactionKind.Refund,
                "meta-hosting:" + fingerprint, legs, registry.Snapshot.Owner, requestId, AuditSource: "meta.hosting", ParticipantWrites: writes, Authority: authority));
            success = result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate;

            void Add(NCAccountRecord account, long amount)
            {
                amounts.TryGetValue(account.Snapshot.AccountId, out var previous);
                amounts[account.Snapshot.AccountId] = (account.AggregateVersion, checked(previous.Amount + amount));
            }
        }
        if (!success)
            return Failure("nc-meta-hosting-conflict");
        var committed = await Read<NCMetaHostingLease>(LeaseType, lease.Id.ToString("D"));
        if (committed is null || committed.Status == NCMetaHostingLeaseStatus.Active)
            return Failure("nc-meta-hosting-conflict");
        await Main(() => { if (CurrentEpoch(stamp.epoch)) { _revocations++; Cache(committed); } return true; });
        return new(true, "nc-meta-hosting-settled", Lease: Summary(committed));
    }

    public async Task<NCMetaHostingLease?> ResolveDomainAsync(string domain)
    {
        if (!MuiCitiNetAddress.IsDomain(domain))
            return null;
        var stamp = await Main(() => EnsureEpoch() ? (Epoch: _worldEpoch, Revocations: _revocations) : default);
        if (!stamp.Epoch.IsValid) return null;
        var pointer = await Read<NCMetaHostingDomain>(DomainType, domain);
        if (pointer is null)
            return null;
        var lease = await Read<NCMetaHostingLease>(LeaseType, pointer.LeaseId.ToString("D"));
        if (lease is null || lease.Domain != domain || lease.Buyer != pointer.Owner)
            return null;
        return await Main(() =>
        {
            if (!CurrentEpoch(stamp.Epoch) || _revocations != stamp.Revocations) return false;
            Cache(lease); return true;
        }) ? lease : null;
    }

    public bool CanUseLease(Guid id, long revision) => !_revoking.Contains(id) && _active.TryGetValue(id, out var lease) && lease.Revision == revision &&
        lease.Status == NCMetaHostingLeaseStatus.Active && _persistence.TryCaptureTime(out var epoch, out var time) && epoch == _worldEpoch && time.Ticks < lease.ExpiresAtTicks &&
        _devices.TryResolve(lease.HostDeviceId, out var device) && device.Owner == lease.Provider &&
        _applications.TryGetRegistration(device.Entity, lease.PackageDigest, out var app) && app.Owner == lease.Provider &&
        app.Application.Views.ContainsKey(lease.View);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!EnsureEpoch() || !_persistence.TryCaptureTime(out _, out var time))
            return;
        if (!_loaded && !_loading)
        {
            _loading = true;
            _ = LoadPageAsync(_worldEpoch, _revocations, _cursor);
        }
        if (time.Ticks < _nextAudit)
            return;
        _nextAudit = time.Ticks + TimeSpan.FromSeconds(5).Ticks;
        var inspections = Math.Min(64, _audit.Count);
        for (var i = 0; i < inspections && _audit.TryDequeue(out var id); i++)
        {
            if (!_active.TryGetValue(id, out var lease))
                continue;
            _audit.Enqueue(id);
            RefreshQuota(lease);
            if (_settling.Count >= 8)
                break;
            if (_settling.Contains(lease.Id))
                continue;
            if (time.Ticks >= lease.ExpiresAtTicks)
            {
                _settling.Add(lease.Id);
                _ = AutomaticSettle(lease, NCMetaHostingLeaseStatus.Expired);
            }
            else if (lease.Units > 0 && !CanUseLease(lease.Id, lease.Revision))
            {
                if (!_defaultSince.TryGetValue(lease.Id, out var since))
                    _defaultSince[lease.Id] = time.Ticks;
                else if (time.Ticks - since >= TimeSpan.FromSeconds(60).Ticks)
                {
                    _settling.Add(lease.Id);
                    _ = AutomaticSettle(lease, NCMetaHostingLeaseStatus.ProviderDefault);
                }
            }
            else
                _defaultSince.Remove(lease.Id);
        }
    }

    private async Task AutomaticSettle(NCMetaHostingLease lease, NCMetaHostingLeaseStatus status)
    {
        var epoch = _worldEpoch;
        try { await SettleAsync(lease, NCPersistentIdentity.Command("meta", "hosting-auto-settle", lease.Id.ToString("D") + ":" + (byte) status), status); }
        catch (Exception exception) { Log.Error($"META hosting settlement failed: {exception}"); }
        finally { await Main(() => { if (_worldEpoch == epoch) _settling.Remove(lease.Id); return true; }); }
    }

    private async Task LoadPageAsync(NCSessionEpoch expectedEpoch, ulong revocations, string? cursor)
    {
        try
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(LeaseType, cursor, 64);
            var leases = new List<NCMetaHostingLease>();
            foreach (var candidate in page)
            {
                // Enumeration supplies only IDs/cursor. Authority comes from the selected checkpoint and its verified tail.
                var snapshot = await _persistence.ReadSnapshotAsync(LeaseType, candidate.AggregateId);
                if (snapshot is null) continue;
                if (snapshot.FormatVersion != 1 || snapshot.Payload.Length > 64 * 1024) continue;
                try
                {
                    var lease = JsonSerializer.Deserialize<NCMetaHostingLease>(snapshot.Payload, Json);
                    if (lease is not null && Valid(lease, snapshot.AggregateId, snapshot.AggregateVersion)) leases.Add(lease);
                }
                catch (JsonException) { }
            }
            await Main(() =>
            {
                if (!CurrentEpoch(expectedEpoch)) return false;
                if (_revocations != revocations) { _loading = false; return false; }
                foreach (var lease in leases)
                    Cache(lease);
                _cursor = page.LastOrDefault()?.AggregateId ?? _cursor;
                _loaded = page.Count < 64;
                _loading = false;
                return true;
            });
        }
        catch (Exception exception)
        {
            Log.Error($"META hosting recovery failed: {exception}");
            await Main(() => { if (CurrentEpoch(expectedEpoch)) _loading = false; return true; });
        }
    }

    private void Cache(NCMetaHostingLease lease)
    {
        if (_versions.TryGetValue(lease.Id, out var revision) && revision > lease.Revision)
            return;
        if (!_versions.ContainsKey(lease.Id))
        {
            while (_versionOrder.Count >= 4096)
            {
                var old = _versionOrder.Dequeue();
                if (_active.ContainsKey(old)) _versionOrder.Enqueue(old);
                else _versions.Remove(old);
            }
            _versionOrder.Enqueue(lease.Id);
        }
        _versions[lease.Id] = lease.Revision;
        if (lease.Status == NCMetaHostingLeaseStatus.Active)
        {
            if (_active.Count >= 1024 && !_active.ContainsKey(lease.Id))
                return;
            if (!_active.ContainsKey(lease.Id))
                _audit.Enqueue(lease.Id);
            _active[lease.Id] = lease;
            _domains[lease.Domain] = lease;
            RefreshQuota(lease);
        }
        else
        {
            _active.Remove(lease.Id);
            _applications.ReleaseHostingSessions(lease.Id);
            if (_domains.TryGetValue(lease.Domain, out var current) && current.Id == lease.Id)
                _domains.Remove(lease.Domain);
            _defaultSince.Remove(lease.Id);
        }
    }

    public NCMetaHostingLeaseSummary[] GetCachedLeases(string owner) => _active.Values.Where(x => x.Buyer == owner).Take(64).Select(Summary).ToArray();
    public async Task<NCMetaHostingLeaseSummary[]> ReadOwnerLeasesAsync(string owner)
    {
        if (!NCPartyRef.TryParse(owner, out _)) return [];
        var stamp = await Main(() => EnsureEpoch() ? (Epoch: _worldEpoch, Revocations: _revocations) : default);
        if (!stamp.Epoch.IsValid) return [];
        var index = await Read<NCMetaHostingOwnerIndex>(OwnerType, owner);
        if (index is null) return [];
        var leases = new List<NCMetaHostingLease>();
        foreach (var id in index.Leases)
        {
            var lease = await Read<NCMetaHostingLease>(LeaseType, id.ToString("D"));
            if (lease is not null && lease.Buyer == owner) leases.Add(lease);
        }
        if (!await Main(() =>
        {
            if (!CurrentEpoch(stamp.Epoch) || _revocations != stamp.Revocations) return false;
            foreach (var lease in leases) Cache(lease); return true;
        })) return [];
        return leases.Select(Summary).ToArray();
    }
    private bool EnsureEpoch()
    {
        if (!_persistence.TryCaptureTime(out var epoch, out _)) return false;
        if (epoch == _worldEpoch) return true;
        foreach (var id in _active.Keys) _applications.ReleaseHostingSessions(id);
        _active.Clear(); _versions.Clear(); _versionOrder.Clear(); _audit.Clear(); _domains.Clear(); _defaultSince.Clear(); _settling.Clear(); _revoking.Clear();
        _worldEpoch = epoch; _revocations++; _cursor = null; _loaded = _loading = false; _nextAudit = 0;
        return true;
    }
    private bool CurrentEpoch(NCSessionEpoch expected) => EnsureEpoch() && expected == _worldEpoch;
    private void RefreshQuota(NCMetaHostingLease lease)
    {
        if (lease.Units <= 0) return;
        if (CanUseLease(lease.Id, lease.Revision) && _devices.TryResolve(lease.HostDeviceId, out var device) &&
            _applications.TryGetRegistration(device.Entity, lease.PackageDigest, out var app))
            _applications.TryReserveHostingSessions(device.Entity, lease.Id, lease.MaxSessions, () => CanUseLease(lease.Id, lease.Revision), app.RuntimeGeneration);
        else _applications.ReleaseHostingSessions(lease.Id);
    }
    private async Task<T?> Read<T>(string type, string id) where T : class
    {
        var snapshot = await _persistence.ReadSnapshotAsync(type, id);
        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Payload.Length > 64 * 1024) return null;
        try
        {
            var value = JsonSerializer.Deserialize<T>(snapshot.Payload, Json);
            return value is not null && Valid(value, id, snapshot.AggregateVersion) ? value : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool Valid(object value, string id, long revision)
    {
        static bool Party(string? party) => party is not null && NCPartyRef.TryParse(party, out _);
        static bool Host(string? host) => Guid.TryParseExact(host, "D", out var guid) && guid != Guid.Empty && host == guid.ToString("D");
        static bool Digest(string? digest) => digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool View(string? view) => view is not null && MuiBinding.IsPath(view);
        static bool Domain(string? domain) => domain is not null && MuiCitiNetAddress.IsDomain(domain);
        return revision > 0 && (value switch
        {
            NCMetaHostingOffer x => x.Revision == revision && x.HostDeviceId == id && Host(x.HostDeviceId) && Party(x.Provider) &&
                Digest(x.PackageDigest) && View(x.View) && x.Profile == ProfileId.Id,
            NCMetaHostingQuote x => x.Revision == revision && (x.PoolSlot is { } slot ? slot is >= 0 and < MetaQuotePoolPolicy.Slots && id == MetaQuotePoolPolicy.SlotKey(slot) : x.Id.ToString("D") == id) && x.Id != Guid.Empty && Party(x.Buyer) && Party(x.Provider) &&
                Domain(x.Domain) && Host(x.HostDeviceId) && Digest(x.PackageDigest) && View(x.View) && x.Units is >= 0 and <= 16 &&
                x.DomainFee is > 0 and <= 1000000 && x.HostingFee is >= 0 and <= 16000000 && x.TermTicks is >= 600000000 and <= 864000000000 &&
                x.MaxSessions is >= 1 and <= 64 && x.ExpiresAtTicks >= 0 && x.OfferRevision >= 0 && x.Consumed == (x.LeaseId != Guid.Empty),
            NCMetaHostingLease x => x.Revision == revision && x.Id.ToString("D") == id && x.Id != Guid.Empty && x.QuoteId != Guid.Empty && Party(x.Buyer) && Party(x.Provider) &&
                Domain(x.Domain) && Host(x.HostDeviceId) && Digest(x.PackageDigest) && View(x.View) && x.Units is >= 0 and <= 16 &&
                x.MaxSessions is >= 1 and <= 64 && x.StartsAtTicks >= 0 && x.ExpiresAtTicks > x.StartsAtTicks &&
                x.ExpiresAtTicks - x.StartsAtTicks <= TimeSpan.FromDays(1).Ticks && x.DomainFee is > 0 and <= 1000000 &&
                x.HostingFee is >= 0 and <= 16000000 && x.PaymentTransactionId != Guid.Empty && Enum.IsDefined(x.Status) &&
                (x.Status == NCMetaHostingLeaseStatus.Active ? x.SettlementTransactionId is null : x.SettlementTransactionId is not null),
            NCMetaHostingDomain x => x.Revision == revision && x.Domain == id && Domain(x.Domain) &&
                x.LeaseId != Guid.Empty && Party(x.Owner) && x.ExpiresAtTicks >= 0,
            NCMetaHostingCapacity x => x.Revision == revision && x.HostDeviceId == id && Host(x.HostDeviceId) && x.Reservations is { Count: <= 64 } &&
                x.Reservations.All(p => p.Key != Guid.Empty && p.Value is >= 1 and <= 16) && x.Reservations.Values.Sum() <= 64,
            NCMetaHostingOwnerIndex x => x.Revision == revision && x.Owner == id && Party(x.Owner) && x.Leases is { Length: <= 64 } &&
                x.Leases.All(guid => guid != Guid.Empty) && x.Leases.Distinct().Count() == x.Leases.Length,
            _ => false,
        });
    }
    private async Task<NCAccountRecord?> ServiceAccount(string id, string nameKey)
    {
        var party = NCPersistentIdentity.CityService(id);
        await _directory.RegisterAsync(new(NCPersistentIdentity.Command("meta", "hosting-service", id), party, Loc.GetString(nameKey),
            NCPersistentIdentity.Correlation("meta", "hosting-service", id), AuditSource: "meta.hosting"));
        return (await _finance.EnsurePrimaryAccountAsync(party, "meta.hosting")).Account;
    }
    private Task<T> Main<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>();
        _tasks.RunOnMainThread(() =>
        {
            try { result.SetResult(action()); }
            catch (Exception exception) { result.SetException(exception); }
        });
        return result.Task;
    }
    private static NCAtomicLedgerParticipantWrite Write<T>(string type, string id, long revision, T value) => new(Guid.NewGuid(), "meta.hosting.changed",
        type, id, revision, 1, JsonSerializer.Serialize(value, Json), "{}");
    private static string Fingerprint(params string[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    private static bool ValidProfile(NCMetaHostingProfilePrototype profile) => profile.DomainFee is > 0 and <= 1000000 && profile.HostingUnitFee is > 0 and <= 1000000 &&
        profile.TermSeconds is >= 60 and <= 86400 && profile.QuoteSeconds is >= 5 and <= 120 && profile.CapacityUnits is >= 1 and <= 64 &&
        profile.SessionsPerUnit is >= 1 and <= 64 && profile.CapacityUnits * profile.SessionsPerUnit <= 64;
    private static NCMetaHostingOutcome Failure(string key) => new(false, key);
    private static NCMetaHostingQuoteSummary Summary(NCMetaHostingQuote quote) => new(quote.Id, quote.Domain, quote.HostDeviceId, quote.View,
        quote.DomainFee, quote.HostingFee, quote.Units, (int) TimeSpan.FromTicks(quote.TermTicks).TotalSeconds, quote.ExpiresAtTicks);
    private static NCMetaHostingLeaseSummary Summary(NCMetaHostingLease lease) => new(lease.Id, lease.Domain, lease.HostDeviceId, lease.View,
        lease.Units, lease.ExpiresAtTicks, lease.Status switch
        {
            NCMetaHostingLeaseStatus.Active => "nc-meta-hosting-active", NCMetaHostingLeaseStatus.Expired => "nc-meta-hosting-expired",
            NCMetaHostingLeaseStatus.ProviderDefault => "nc-meta-hosting-default", _ => "nc-meta-hosting-settled",
        });
}
