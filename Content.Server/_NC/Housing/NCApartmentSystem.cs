// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server.GameTicking;
using Content.Server.Gibbing.Systems;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Components;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Housing;

/// <summary>
/// Runtime Housing facade. Persistent tenure, access and rent deadlines live only in the common property aggregate;
/// this system coordinates those facts with round-local locks, key cards, interiors and bounded rent scheduling.
/// </summary>
public sealed partial class NCApartmentSystem : EntitySystem
{
    private static readonly ProtoId<NCPropertyRuntimeConfigurationPrototype> DefaultRuntimeConfiguration = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPropertyRegistrySystem _registry = default!;
    [Dependency] private NCPropertySettlementSystem _settlements = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _propertyMarket = default!;
    [Dependency] private NCPropertyMarketSettlementSystem _propertyMarketSettlement = default!;
    [Dependency] private NCHousingServiceSystem _housingService = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;
    [Dependency] private NCPropertySnapshotSystem _snapshots = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    private readonly Dictionary<NCApartmentId, NCPropertyAggregateRecord> _properties = [];
    private readonly Dictionary<NCApartmentId, HashSet<EntityUid>> _locks = [];
    private readonly Dictionary<NCZoneDeploymentId, PendingInterior> _pendingInteriors = [];
    private readonly Dictionary<NCApartmentId, NCZoneDeploymentId> _runtimeInteriors = [];
    private readonly Dictionary<NCZoneDeploymentId, PendingRenovation> _renovationsWaitingForClose = [];
    private readonly HashSet<NCApartmentId> _rentProcessing = [];
    private readonly PriorityQueue<RentDueEntry, long> _rentQueue = new();

    private int _rentOperationsInFlight;
    private bool _roundSnapshotsCaptured;
    private bool _initializing;

    private NCPropertyRuntimeConfigurationPrototype RuntimeConfiguration =>
        _prototypes.Index(DefaultRuntimeConfiguration);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd, before: [typeof(GibOnRoundEndSystem)]);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart, before: [typeof(NCZoneTemplateSystem)]);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<NCPropertyLockComponent, ComponentStartup>(OnLockStartup);
        SubscribeLocalEvent<NCPropertyLockComponent, ComponentShutdown>(OnLockShutdown);
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentStateChanged);
        SubscribeLocalEvent<NCPropertyAggregatePersistedEvent>(OnCanonicalPropertyPersisted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;

        var configuration = RuntimeConfiguration;
        var started = 0;
        while (started < configuration.MaximumRentOperationsPerTick &&
               _rentOperationsInFlight < configuration.MaximumConcurrentRentOperations &&
               _rentQueue.TryPeek(out var entry, out var dueTicks) && dueTicks <= now.Ticks)
        {
            _rentQueue.Dequeue();
            if (!_properties.TryGetValue(entry.PropertyId, out var current) ||
                current.AggregateVersion != entry.AggregateVersion ||
                !TryGetProcessingTime(current.Snapshot, out var expected) || expected.Ticks != dueTicks ||
                !_rentProcessing.Add(entry.PropertyId))
            {
                continue;
            }

            started++;
            _rentOperationsInFlight++;
            ProcessScheduledRentAsync(current);
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _roundSnapshotsCaptured = false;
        _ = InitializePropertiesAsync();
    }

    private async Task InitializePropertiesAsync()
    {
        if (_initializing)
            return;

        _initializing = true;
        _snapshots.BeginRoundLoad();
        try
        {
            if (!await _housingService.EnsureReadyAsync("housing-round-initialization"))
            {
                Log.Error("Canonical Housing service could not be initialized.");
                return;
            }

            var loaded = new List<NCPropertyAggregateRecord>();
            foreach (var listing in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
            {
                if (!_zones.TryGetApartment(listing.ApartmentId, out var apartment) ||
                    !_zones.TryGetBuilding(new NCBuildingId(apartment.Parent), out _))
                {
                    // A round may intentionally load a map without this prototype's city sector. The listing remains
                    // unavailable until its mapper-authored apartment/building hierarchy is present.
                    Log.Warning($"Property listing {listing.ID} is unavailable because its apartment/building " +
                                "geography is not loaded.");
                    continue;
                }

                var registration = await _registry.RegisterAsync(new NCRegisterPropertyRequest(
                    NCHousingIdentity.Command("register-property", listing.ApartmentId),
                    listing.ApartmentId,
                    new NCBuildingId(apartment.Parent),
                    [listing.ApartmentId.ZoneId],
                    listing.ID,
                    _housingService.Party,
                    listing.PurchasePrice,
                    listing.AccessPolicy.Id,
                    new NCPropertyStorageBudget(
                        listing.MaximumFixtureItems,
                        listing.MaximumIncludedAssets,
                        listing.MaximumPersonalItems,
                        listing.MaximumMaterializedEntities),
                    listing.Interiors.FirstOrDefault()?.Template.Id ?? "empty-blueprint",
                    NCHousingIdentity.Correlation("register-property", listing.ApartmentId),
                    AuditSource: "housing-round-initialization"));
                var property = registration.Property ?? await _registry.GetAsync(listing.ApartmentId, true);
                if (property == null)
                {
                    Log.Error($"Property listing {listing.ID} could not be registered in canonical storage: " +
                              registration.Error);
                    continue;
                }
                if (property.Snapshot.ListingId != listing.ID ||
                    property.Snapshot.BuildingId != new NCBuildingId(apartment.Parent) ||
                    property.Snapshot.AccessPolicyId != listing.AccessPolicy.Id)
                {
                    Log.Error($"Property listing {listing.ID} conflicts with its persisted canonical identity.");
                    continue;
                }

                loaded.Add(property);
                Cache(property);
                await EnsurePersistentListingsAsync(listing, property);
            }

            await _snapshots.LoadPreparedSnapshotsAsync(loaded);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to initialize canonical properties: {exception}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        CaptureActiveRoundSnapshots();
        _snapshots.BeginRoundLoad();
        _properties.Clear();
        _locks.Clear();
        _pendingInteriors.Clear();
        _runtimeInteriors.Clear();
        _renovationsWaitingForClose.Clear();
        _rentProcessing.Clear();
        _rentQueue.Clear();
        _rentOperationsInFlight = 0;
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (_preferences.TryGetSelectedNCCharacterId(ev.Player.UserId, out var characterId))
            _ = ProcessCharacterRentAsync(characterId);
    }

    /// <summary>Retries due rent only for this character; the global due queue never scans characters or entities.</summary>
    public async Task ProcessCharacterRentAsync(NCCharacterId characterId)
    {
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;

        var tenant = NCPartyRef.ForCharacter(characterId);
        var properties = _properties.Values
            .Where(property => IsActiveTenant(property.Snapshot, tenant, now) &&
                               property.Snapshot.NextRentDueAt is { } due && due <= now)
            .ToArray();
        foreach (var property in properties)
        {
            if (!_rentProcessing.Add(property.Snapshot.PropertyId))
                continue;

            try
            {
                await ProcessRentLifecycleAsync(property, now, liveRound: true);
            }
            finally
            {
                _rentProcessing.Remove(property.Snapshot.PropertyId);
            }
        }
    }

    private async void ProcessScheduledRentAsync(NCPropertyAggregateRecord property)
    {
        try
        {
            if (_clock.TryGetCurrentTime(out _, out var now))
                await ProcessRentLifecycleAsync(property, now, liveRound: true);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to process scheduled rent for {property.Snapshot.PropertyId}: {exception}");
        }
        finally
        {
            _rentProcessing.Remove(property.Snapshot.PropertyId);
            _rentOperationsInFlight--;
        }
    }

    private async Task ProcessRentLifecycleAsync(
        NCPropertyAggregateRecord property,
        NCActiveWorldTime now,
        bool liveRound)
    {
        var snapshot = property.Snapshot;
        if (snapshot.Status is not (NCPropertyStatus.Rented or NCPropertyStatus.Delinquent or
                NCPropertyStatus.EvictionPending) ||
            snapshot.NextRentDueAt is not { } due || now < due ||
            !_prototypes.TryIndex<NCRentPolicyPrototype>(snapshot.RentPolicyId, out var rentPolicy))
            return;

        var tenant = snapshot.Occupants
            .Where(value => value.Kind == NCPropertyOccupancyKind.Tenancy &&
                            (value.ValidUntil == null || value.ValidUntil > now))
            .OrderBy(value => value.StartedAt.Ticks)
            .FirstOrDefault();
        if (tenant == null)
            return;

        NCActiveWorldTime graceEnd;
        try
        {
            graceEnd = due.Advance(rentPolicy.GracePeriod);
        }
        catch (OverflowException)
        {
            Log.Error($"Rent grace deadline overflowed for property {snapshot.PropertyId}.");
            return;
        }

        if (now > graceEnd)
        {
            var evictionCurrent = property;
            if (snapshot.Status != NCPropertyStatus.EvictionPending)
            {
                var pending = await UpdatePropertyAsync(
                    property,
                    property.Snapshot with
                    {
                        Status = NCPropertyStatus.EvictionPending,
                        EvictionAt = now,
                    },
                    property.Snapshot.Owner,
                    "living-city.property.eviction-pending",
                    "housing-eviction-pending");
                if (pending.Property == null)
                    return;
                Cache(pending.Property);
                evictionCurrent = pending.Property;
            }
            var evicted = await EvictAsync(evictionCurrent);
            if (evicted.Property != null)
                Cache(evicted.Property);
            if (evicted.Result != NCPropertyMutationResult.Success || !liveRound)
                return;

            _snapshots.ConfiscateLiveContents(snapshot);
            if (_runtimeInteriors.TryGetValue(snapshot.PropertyId, out var deploymentId) &&
                !_templates.TryCloseDeployment(deploymentId, out var error))
            {
                Log.Error($"Evicted property {snapshot.PropertyId} could not close its interior: {error}");
            }
            _runtimeInteriors.Remove(snapshot.PropertyId);
            await _snapshots.DeleteSnapshotAsync(snapshot);
            return;
        }

        var renewal = await _settlements.RenewAsync(new NCRenewPropertyRentSettlementRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            snapshot.PropertyId,
            property.AggregateVersion,
            tenant.Occupant,
            rentPolicy.RentAmount,
            rentPolicy.BillingInterval.Ticks,
            Guid.NewGuid(),
            AuditSource: "housing-rent-renewal"));
        if (renewal.Property != null)
            Cache(renewal.Property);
        if (renewal.Status is NCPropertySettlementStatus.Applied or NCPropertySettlementStatus.Duplicate)
            return;
        if (renewal.Status != NCPropertySettlementStatus.InsufficientFunds)
            return;

        var current = renewal.Property ?? await _registry.GetAsync(snapshot.PropertyId, true);
        if (current == null)
            return;
        var delinquent = await UpdatePropertyAsync(
            current,
            current.Snapshot with
            {
                Status = NCPropertyStatus.Delinquent,
                DelinquentSince = now,
                EvictionAt = graceEnd,
            },
            tenant.Occupant,
            "living-city.property.rent-delinquent",
            "housing-rent-delinquent");
        if (delinquent.Property != null)
            Cache(delinquent.Property);
    }

    private void OnRoundEnd(RoundEndMessageEvent ev)
    {
        CaptureActiveRoundSnapshots();
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent ev)
    {
        // RoundEndMessage normally captures first. This fallback makes the durability contract independent from
        // game-ticker event ordering and registers the already-running write with the active-world close barrier.
        CaptureActiveRoundSnapshots();
        ev.AddPendingWrite(_snapshots.WaitForPendingSaveAsync());
    }

    private void CaptureActiveRoundSnapshots()
    {
        if (_roundSnapshotsCaptured)
            return;

        var active = new List<NCPropertySnapshotCapture>();
        foreach (var (propertyId, deploymentId) in _runtimeInteriors)
        {
            if (_properties.TryGetValue(propertyId, out var property))
                active.Add(new NCPropertySnapshotCapture(property.Snapshot, deploymentId));
        }

        _snapshots.CaptureRoundSnapshots(active);
        _roundSnapshotsCaptured = true;
    }

    public bool TryGetProperty(NCApartmentId propertyId, out NCPropertyAggregateRecord property) =>
        _properties.TryGetValue(propertyId, out property!);

    public async Task<NCPropertyRuntimeMutationResult> TryRentAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        EntityUid cardRecipient)
    {
        if (!TryGetListing(propertyId, out var listing) ||
            !TryGetInteriorOffer(listing, templateId, out var offer, out var template, out var rentPolicy) ||
            !_prototypes.TryIndex(listing.AccessPolicy, out NCPropertyAccessPolicyPrototype? accessPolicy) ||
            !_properties.TryGetValue(propertyId, out var current))
            return Failed(NCPropertyMutationResult.InvalidRequest);

        var tenant = NCPartyRef.ForCharacter(characterId);
        var stableListing = $"rent.{listing.ID}.{offer.Template.Id}";
        var listingId = NCPersistentIdentity.PropertyListing(propertyId, stableListing);
        var marketListing = await _propertyMarket.GetListingAsync(listingId, true);
        if (marketListing == null || marketListing.Snapshot.Status != NCPropertyListingStatus.Open)
            return Failed(NCPropertyMutationResult.NotAvailable, current);
        var policy = _prototypes.Index<NCPropertyMarketPolicyPrototype>("Default");
        var quoteStable = $"{characterId}.{current.AggregateVersion}.{marketListing.AggregateVersion}";
        var quoteId = NCPersistentIdentity.PropertyQuote(listingId, tenant, quoteStable);
        var quoteResult = await _propertyMarket.CreateQuoteAsync(new NCCreatePropertyQuoteRequest(
            NCPersistentIdentity.Command("housing", "quote", quoteStable), quoteId, listingId,
            marketListing.AggregateVersion, tenant, policy.MaximumQuoteLifetime,
            NCPersistentIdentity.Correlation("housing", "quote", quoteStable),
            marketListing.Snapshot.LastAppliedEventId, "housing-player-rent"));
        if (quoteResult.Quote == null)
            return Failed(NCPropertyMutationResult.Conflict, current);
        var reservationId = NCPersistentIdentity.PropertyReservation(quoteId, "primary");
        var reservationResult = await _propertyMarket.ReserveAsync(new NCReservePropertyQuoteRequest(
            NCPersistentIdentity.Command("housing", "reserve", reservationId.ToString()), reservationId, quoteId,
            quoteResult.Quote.AggregateVersion, policy.MaximumReservationLifetime, tenant,
            NCPersistentIdentity.Correlation("housing", "reserve", reservationId.ToString()),
            quoteResult.Quote.Snapshot.LastAppliedEventId, "housing-player-rent"));
        if (reservationResult.Reservation == null)
            return Failed(NCPropertyMutationResult.Conflict, current);
        var grantId = NCPersistentIdentity.Command("housing", "access-grant", reservationId.ToString());
        var settlement = await _propertyMarketSettlement.SettleAsync(new NCSettlePropertyReservationRequest(
            NCPersistentIdentity.Command("housing", "settle", reservationId.ToString()),
            NCPersistentIdentity.Transaction("housing", "rent", reservationId.ToString()), reservationId,
            reservationResult.Reservation.AggregateVersion, tenant, grantId,
            NCPersistentIdentity.Correlation("housing", "settle", reservationId.ToString()),
            reservationResult.Reservation.Snapshot.LastAppliedEventId, "housing-player-rent"));

        var runtime = FromMarketSettlement(settlement, grantId);
        ApplyMutation(runtime, accessPolicy, cardRecipient);
        return runtime;
    }

    private async Task EnsurePersistentListingsAsync(
        NCPropertyListingPrototype source,
        NCPropertyAggregateRecord property)
    {
        if (!_prototypes.TryIndex(source.AccessPolicy, out NCPropertyAccessPolicyPrototype? accessPolicy))
            return;
        if (source.AllowsPurchase)
        {
            var stable = $"sale.{source.ID}";
            var result = await _propertyMarket.CreateListingAsync(new NCCreatePropertyListingRequest(
                NCPersistentIdentity.Command("housing", "seed-listing", stable),
                NCPersistentIdentity.PropertyListing(source.ApartmentId, stable), source.ID,
                source.ApartmentId, property.AggregateVersion, NCPropertyOfferKind.Sale, source.PurchasePrice, 0,
                string.Empty, 0, null, null, NCPropertyPermission.All, null, property.Snapshot.Owner,
                NCPersistentIdentity.Correlation("housing", "seed-listing", stable),
                property.Snapshot.LastAppliedEventId, "housing-listing-bootstrap"));
            if (result.Status is not (NCPropertyAggregateMutationStatus.Applied or
                NCPropertyAggregateMutationStatus.Duplicate))
                Log.Error($"Sale listing for property {source.ApartmentId} could not be seeded: {result.Error}");
            else
                await ReopenStableListingIfAvailableAsync(result.Listing, property, stable);
        }
        if (!source.AllowsRent)
            return;
        foreach (var offer in source.Interiors)
        {
            if (!_prototypes.TryIndex(offer.Template, out NCZoneTemplatePrototype? template) ||
                !_prototypes.TryIndex(offer.RentPolicy, out NCRentPolicyPrototype? rentPolicy))
                continue;
            var stable = $"rent.{source.ID}.{offer.Template.Id}";
            var result = await _propertyMarket.CreateListingAsync(new NCCreatePropertyListingRequest(
                NCPersistentIdentity.Command("housing", "seed-listing", stable),
                NCPersistentIdentity.PropertyListing(source.ApartmentId, stable), source.ID,
                source.ApartmentId, property.AggregateVersion, NCPropertyOfferKind.Rent, rentPolicy.RentAmount,
                source.SecurityDeposit, offer.RentPolicy.Id, rentPolicy.BillingInterval.Ticks, offer.Template.Id,
                template.Revision, accessPolicy.TenantPermissions, null, property.Snapshot.Owner,
                NCPersistentIdentity.Correlation("housing", "seed-listing", stable),
                property.Snapshot.LastAppliedEventId, "housing-listing-bootstrap"));
            if (result.Status is not (NCPropertyAggregateMutationStatus.Applied or
                NCPropertyAggregateMutationStatus.Duplicate))
                Log.Error($"Rental listing {stable} could not be seeded: {result.Error}");
            else
                await ReopenStableListingIfAvailableAsync(result.Listing, property, stable);
        }
    }

    private async Task ReopenStableListingIfAvailableAsync(
        NCPropertyListingRecord? listing,
        NCPropertyAggregateRecord property,
        string stable)
    {
        if (listing == null || listing.Snapshot.Status == NCPropertyListingStatus.Open ||
            listing.Snapshot.Status is not (NCPropertyListingStatus.Fulfilled or NCPropertyListingStatus.Expired) ||
            property.Snapshot.Status != NCPropertyStatus.Available ||
            property.Snapshot.Owner != listing.Snapshot.Seller)
            return;
        var suffix = $"{stable}.{listing.AggregateVersion}";
        var result = await _propertyMarket.ReopenListingAsync(new NCReopenPropertyListingRequest(
            NCPersistentIdentity.Command("housing", "reopen-listing", suffix), listing.Snapshot.ListingId,
            listing.AggregateVersion, property.AggregateVersion, property.Snapshot.Owner,
            NCPersistentIdentity.Correlation("housing", "reopen-listing", suffix),
            property.Snapshot.LastAppliedEventId, "housing-listing-lifecycle"));
        if (result.Status is not (NCPropertyAggregateMutationStatus.Applied or
            NCPropertyAggregateMutationStatus.Duplicate))
            Log.Error($"Stable property listing {stable} could not be reopened: {result.Error}");
    }

    public async Task<NCPropertyRuntimeMutationResult> TryAssignStartingHousingAsync(
        NCCharacterId characterId,
        EntityUid cardRecipient)
    {
        var tenant = NCPartyRef.ForCharacter(characterId);
        if (_properties.Values.Any(property => property.Snapshot.Occupants.Any(value =>
                value.Kind == NCPropertyOccupancyKind.Tenancy && value.Occupant == tenant)))
            return Failed(NCPropertyMutationResult.Conflict);

        foreach (var listing in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
        {
            if (!listing.StartingHousingEligible ||
                !_properties.TryGetValue(listing.ApartmentId, out var property) ||
                property.Snapshot.Status != NCPropertyStatus.Available ||
                listing.Interiors.FirstOrDefault() is not { } offer)
                continue;

            var result = await TryRentAsync(listing.ApartmentId, characterId, offer.Template, cardRecipient);
            if (result.Result == NCPropertyMutationResult.Success)
                return result;
        }

        return Failed(NCPropertyMutationResult.NotAvailable);
    }

    public async Task<NCPropertyRuntimeMutationResult> TryReleaseAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        NCPropertyReleaseReason reason)
    {
        if (!TryGetListing(propertyId, out _) || reason == NCPropertyReleaseReason.Sale ||
            !_properties.TryGetValue(propertyId, out var current))
            return Failed(NCPropertyMutationResult.InvalidRequest);

        var actor = NCPartyRef.ForCharacter(characterId);
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            !HasPermission(current.Snapshot, actor, NCPropertyPermission.ManageTenants, now))
            return Failed(NCPropertyMutationResult.NotAuthorized, current);
        if (reason == NCPropertyReleaseReason.VoluntaryMoveOut &&
            (_snapshots.HasResidentItems(current.Snapshot) || _occupancy.IsOccupied(propertyId.ZoneId, out _)))
            return Failed(NCPropertyMutationResult.Conflict, current);

        var deploymentToClose = _runtimeInteriors.GetValueOrDefault(propertyId);
        if (!deploymentToClose.IsValid)
            deploymentToClose = current.Snapshot.ActiveDeploymentId;

        var refund = _prototypes.TryIndex<NCRentPolicyPrototype>(current.Snapshot.RentPolicyId, out var rentPolicy)
            ? rentPolicy.VoluntaryDepositRefundBasisPoints
            : 10_000;
        var settlement = await _settlements.ReleaseLeaseAsync(new NCReleasePropertyLeaseSettlementRequest(
            NCPersistentIdentity.Command("housing", "release-lease", $"{propertyId}.{current.AggregateVersion}"),
            NCPersistentIdentity.Transaction("housing", "release-lease", $"{propertyId}.{current.AggregateVersion}"),
            propertyId, current.AggregateVersion, actor, actor, reason, refund,
            NCPersistentIdentity.Correlation("housing", "release-lease", $"{propertyId}.{current.AggregateVersion}"),
            current.Snapshot.LastAppliedEventId, $"housing-release-{reason.ToString().ToLowerInvariant()}"));
        var result = FromSettlement(settlement, Guid.Empty);
        if (result.Property != null)
            Cache(result.Property);
        if (result.Result != NCPropertyMutationResult.Success)
            return result;

        if (deploymentToClose.IsValid && !_templates.TryCloseDeployment(deploymentToClose, out var error))
            Log.Error($"Property {propertyId} was released but interior cleanup could not start: {error}");
        await _snapshots.DeleteSnapshotAsync(current.Snapshot);
        if (result.Property != null && TryGetListing(propertyId, out var source))
            await EnsurePersistentListingsAsync(source, result.Property);
        return result;
    }

    public async Task<NCPropertyRuntimeMutationResult> TryRevokeAccessGrantAsync(
        NCApartmentId propertyId,
        NCPartyRef actor,
        Guid grantId)
    {
        if (!_properties.TryGetValue(propertyId, out var current) || grantId == Guid.Empty ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !HasPermission(current.Snapshot, actor, NCPropertyPermission.ManageCards, now) ||
            current.Snapshot.AccessGrants.All(grant => grant.GrantId != grantId))
            return Failed(NCPropertyMutationResult.NotAuthorized, current);
        var revision = checked(current.Snapshot.AccessRevision + 1);
        var grants = current.Snapshot.AccessGrants.Where(grant => grant.GrantId != grantId)
            .Select(grant => grant with { AccessRevision = revision }).ToArray();
        var result = await UpdatePropertyAsync(current, current.Snapshot with
            {
                AccessRevision = revision,
                AccessGrants = grants,
            }, actor, "living-city.property.access-revoked", "housing-access-revoke");
        if (result.Property != null)
            Cache(result.Property);
        return result;
    }

    public async Task<NCPropertyRuntimeMutationResult> TryAddOccupantAsync(
        NCApartmentId propertyId,
        NCPartyRef actor,
        NCPartyRef subject,
        NCPropertyOccupancyKind kind,
        NCActiveWorldTime? validUntil)
    {
        if (!_properties.TryGetValue(propertyId, out var current) || !subject.IsValid ||
            kind is not (NCPropertyOccupancyKind.Resident or NCPropertyOccupancyKind.Guest) ||
            !_clock.TryGetCurrentTime(out _, out var now) || validUntil is { } expiry && expiry <= now ||
            !HasPermission(current.Snapshot, actor, NCPropertyPermission.ManageTenants, now) ||
            current.Snapshot.Occupants.Any(occupant => occupant.Occupant == subject))
            return Failed(NCPropertyMutationResult.InvalidRequest, current);
        var result = await UpdatePropertyAsync(current, current.Snapshot with
            {
                Occupants = current.Snapshot.Occupants.Append(
                    new NCPropertyOccupantSnapshot(subject, kind, now, validUntil)).ToArray(),
            }, actor, "living-city.property.occupant-added", "housing-occupant-add");
        if (result.Property != null)
            Cache(result.Property);
        return result;
    }

    public async Task<NCPropertyRuntimeMutationResult> TryRemoveOccupantAsync(
        NCApartmentId propertyId,
        NCPartyRef actor,
        NCPartyRef subject)
    {
        if (!_properties.TryGetValue(propertyId, out var current) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !HasPermission(current.Snapshot, actor, NCPropertyPermission.ManageTenants, now) ||
            current.Snapshot.Occupants.All(occupant => occupant.Occupant != subject ||
                occupant.Kind is NCPropertyOccupancyKind.Ownership or NCPropertyOccupancyKind.Tenancy))
            return Failed(NCPropertyMutationResult.NotAuthorized, current);
        var removedGrantIds = current.Snapshot.AccessGrants
            .Where(grant => grant.Subject == subject).Select(grant => grant.GrantId).ToHashSet();
        var revision = removedGrantIds.Count > 0
            ? checked(current.Snapshot.AccessRevision + 1)
            : current.Snapshot.AccessRevision;
        var result = await UpdatePropertyAsync(current, current.Snapshot with
            {
                Occupants = current.Snapshot.Occupants.Where(occupant => occupant.Occupant != subject).ToArray(),
                AccessRevision = revision,
                AccessGrants = current.Snapshot.AccessGrants.Where(grant => !removedGrantIds.Contains(grant.GrantId))
                    .Select(grant => grant with { AccessRevision = revision }).ToArray(),
            }, actor, "living-city.property.occupant-removed", "housing-occupant-remove");
        if (result.Property != null)
            Cache(result.Property);
        return result;
    }

    public async Task<NCPropertyRuntimeMutationResult> TryIssueKeyCardAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        NCCharacterId subjectCharacterId,
        NCPropertyPermission permissions,
        NCActiveWorldTime? validUntil,
        EntityUid cardRecipient)
    {
        if (!TryGetListing(propertyId, out var listing) ||
            !_prototypes.TryIndex(listing.AccessPolicy, out NCPropertyAccessPolicyPrototype? accessPolicy) ||
            !_properties.TryGetValue(propertyId, out var current) ||
            !_clock.TryGetCurrentTime(out _, out var now) || permissions == NCPropertyPermission.None ||
            validUntil is { } expiry && expiry <= now)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        var actor = NCPartyRef.ForCharacter(actorCharacterId);
        var subject = NCPartyRef.ForCharacter(subjectCharacterId);
        if (!current.Snapshot.Occupants.Any(value =>
                value.Kind == NCPropertyOccupancyKind.Tenancy && value.Occupant == actor))
            return Failed(NCPropertyMutationResult.NotAuthorized, current);
        if (current.Snapshot.AccessGrants.Count(value => value.ValidUntil == null || value.ValidUntil > now) >=
            accessPolicy.MaximumActiveCards)
            return Failed(NCPropertyMutationResult.Conflict, current);

        var grant = new NCPropertyAccessGrantSnapshot(
            Guid.NewGuid(),
            subject,
            permissions,
            current.Snapshot.AccessRevision,
            now,
            validUntil);
        var result = await UpdatePropertyAsync(
            current,
            current.Snapshot with { AccessGrants = current.Snapshot.AccessGrants.Append(grant).ToArray() },
            actor,
            "living-city.property.access-granted",
            "housing-access-grant",
            grant);
        ApplyMutation(result, accessPolicy, cardRecipient);
        return result;
    }

    public bool TryInvokeInterior(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        out NCZoneDeploymentId deploymentId,
        out string error)
    {
        deploymentId = default;
        error = string.Empty;
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            !_properties.TryGetValue(propertyId, out var record) ||
            !IsActiveTenant(record.Snapshot, NCPartyRef.ForCharacter(actorCharacterId), now))
        {
            error = "Only the active renter may invoke an interior.";
            return false;
        }

        var property = record.Snapshot;
        if (property.CurrentTemplateId is not { } selected ||
            property.CurrentTemplateRevision is not { } selectedRevision)
        {
            error = "This property does not have a selected interior.";
            return false;
        }

        var templateId = new ProtoId<NCZoneTemplatePrototype>(selected);
        if (!TryGetListing(propertyId, out var listing) ||
            !listing.Interiors.Any(value => value.Template == templateId) ||
            !_prototypes.TryIndex(templateId, out NCZoneTemplatePrototype? template) ||
            template.TargetZoneId != propertyId.ZoneId || template.Revision != selectedRevision)
        {
            error = "The selected interior revision is unavailable.";
            return false;
        }

        return TryDeployInterior(record, actorCharacterId, templateId, out deploymentId, out error);
    }

    public bool IsInteriorActiveOrPending(NCApartmentId propertyId)
    {
        if (_runtimeInteriors.TryGetValue(propertyId, out var runtime) &&
            _templates.TryGetDeployment(runtime, out var deployment) &&
            deployment.State is not NCZoneDeploymentState.Completed and not NCZoneDeploymentState.FailedClean)
            return true;

        return _pendingInteriors.Values.Any(value => value.PropertyId == propertyId);
    }

    public bool AreInteriorSnapshotsReady => _snapshots.IsReady;

    /// <summary>
    /// Buys a mapper-authored interior revision and starts its deployment. Payment and the canonical pending state
    /// commit together; a failed deployment restores the previous property state and refunds the exact price.
    /// </summary>
    public async Task<NCPropertyRuntimeMutationResult> TryRenovateAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        ProtoId<NCZoneTemplatePrototype> templateId)
    {
        if (!_properties.TryGetValue(propertyId, out var current))
            return Failed(NCPropertyMutationResult.Conflict);
        if (!TryGetListing(propertyId, out var listing) ||
            !TryGetInteriorOffer(listing, templateId, out var offer, out var template, out _) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            IsInteriorPending(propertyId) ||
            !HasPermission(current.Snapshot, NCPartyRef.ForCharacter(actorCharacterId),
                NCPropertyPermission.Renovate, now) ||
            _snapshots.HasResidentItems(current.Snapshot) ||
            _occupancy.IsOccupied(propertyId.ZoneId, out _))
            return Failed(NCPropertyMutationResult.Conflict, current);

        var actor = NCPartyRef.ForCharacter(actorCharacterId);
        var stable = $"{propertyId}.{current.AggregateVersion}.{template.Revision}";
        var rollback = new RenovationRollback(
            actor,
            offer.RenovationPrice,
            current.Snapshot.CurrentTemplateId,
            current.Snapshot.CurrentTemplateRevision,
            current.Snapshot.InteriorState,
            current.Snapshot.RenovationRevision);
        var settlement = await _settlements.RenovateAsync(new NCRenovatePropertySettlementRequest(
            NCPersistentIdentity.Command("housing", "renovate", stable),
            NCPersistentIdentity.Transaction("housing", "renovate", stable), propertyId,
            current.AggregateVersion, actor, offer.RenovationPrice, templateId.Id, template.Revision,
            NCPersistentIdentity.Correlation("housing", "renovate", stable),
            current.Snapshot.LastAppliedEventId, "housing-player-renovation"));
        var result = FromSettlement(settlement, Guid.Empty);
        if (result.Property == null || result.Result != NCPropertyMutationResult.Success)
            return result;
        Cache(result.Property);

        var pending = new PendingRenovation(result.Property, actorCharacterId, templateId, rollback);
        var oldDeployment = _runtimeInteriors.GetValueOrDefault(propertyId);
        if (!oldDeployment.IsValid)
            oldDeployment = current.Snapshot.ActiveDeploymentId;
        if (oldDeployment.IsValid)
        {
            _renovationsWaitingForClose[oldDeployment] = pending;
            if (!_templates.TryCloseDeployment(oldDeployment, out _))
            {
                _renovationsWaitingForClose.Remove(oldDeployment);
                await RollbackRenovationAsync(result.Property, rollback, "old-interior-close-failed");
            }
            return result;
        }

        await ContinueRenovationAsync(pending);
        return result;
    }

    private bool IsInteriorPending(NCApartmentId propertyId) =>
        _pendingInteriors.Values.Any(value => value.PropertyId == propertyId) ||
        _renovationsWaitingForClose.Values.Any(value => value.Property.Snapshot.PropertyId == propertyId);

    private bool TryDeployInterior(
        NCPropertyAggregateRecord property,
        NCCharacterId actorCharacterId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        out NCZoneDeploymentId deploymentId,
        out string error)
    {
        deploymentId = default;
        error = string.Empty;
        if (property.Snapshot.CurrentTemplateId != null && !_snapshots.IsReady)
        {
            error = "Persistent apartment snapshots are still loading.";
            return false;
        }
        if (IsInteriorActiveOrPending(property.Snapshot.PropertyId))
        {
            error = "This property's interior is already active or pending.";
            return false;
        }

        var request = new NCZoneDeploymentRequest(
            NCZoneDeploymentId.New(),
            property.Snapshot.PropertyId.ZoneId,
            templateId,
            NCZoneDeploymentPurpose.ApartmentInterior,
            actorCharacterId,
            null);
        if (!_templates.TryRequestDeployment(request, out deploymentId, out error))
            return false;

        _pendingInteriors[deploymentId] = new PendingInterior(
            property.Snapshot.PropertyId,
            templateId.Id,
            property.AggregateVersion,
            null);
        return true;
    }

    private void OnDeploymentStateChanged(ref NCZoneDeploymentStateChangedEvent ev)
    {
        var propertyId = new NCApartmentId(ev.ZoneId);
        if (ev.NewState == NCZoneDeploymentState.Active)
            _runtimeInteriors[propertyId] = ev.DeploymentId;
        else if (ev.NewState is NCZoneDeploymentState.Completed or NCZoneDeploymentState.FailedClean)
            _runtimeInteriors.Remove(propertyId);

        if (ev.NewState is NCZoneDeploymentState.Completed or NCZoneDeploymentState.FailedClean &&
            _renovationsWaitingForClose.Remove(ev.DeploymentId, out var renovation))
        {
            _ = ContinueRenovationAsync(renovation);
            return;
        }

        if (!_pendingInteriors.TryGetValue(ev.DeploymentId, out var pending))
            return;
        if (ev.NewState is NCZoneDeploymentState.FailedClean or NCZoneDeploymentState.Completed)
        {
            _pendingInteriors.Remove(ev.DeploymentId);
            if (pending.Rollback is { } rollback)
                _ = RollbackRenovationAsync(pending.PropertyId, pending.ExpectedVersion, rollback,
                    "new-interior-deployment-failed");
            return;
        }
        if (ev.NewState != NCZoneDeploymentState.Active ||
            !_templates.TryGetDeployment(ev.DeploymentId, out var deployment))
            return;

        _pendingInteriors.Remove(ev.DeploymentId);
        _ = PersistInteriorAsync(ev.DeploymentId, pending, deployment.TemplateRevision);
    }

    private async Task PersistInteriorAsync(
        NCZoneDeploymentId deploymentId,
        PendingInterior pending,
        int templateRevision)
    {
        try
        {
            var current = await _registry.GetAsync(pending.PropertyId, true);
            if (current == null || current.AggregateVersion != pending.ExpectedVersion)
            {
                Log.Error($"Interior deployment {deploymentId} became active after its property version changed.");
                return;
            }

            var actor = current.Snapshot.Occupants
                .FirstOrDefault(value => value.Kind == NCPropertyOccupancyKind.Tenancy)?.Occupant ??
                _housingService.Party;
            var result = await UpdatePropertyAsync(
                current,
                current.Snapshot with
                {
                    ActiveDeploymentId = deploymentId,
                    CurrentTemplateId = pending.TemplateId,
                    CurrentTemplateRevision = templateRevision,
                    InteriorState = "active",
                },
                actor,
                "living-city.property.interior-activated",
                "housing-interior-activation");
            if (result.Property != null)
                Cache(result.Property);
            else
            {
                Log.Error($"Interior deployment {deploymentId} could not update its canonical property.");
                if (pending.Rollback is { } rollback)
                    await RollbackRenovationAsync(pending.PropertyId, pending.ExpectedVersion, rollback,
                        "interior-activation-persist-failed");
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist interior deployment {deploymentId}: {exception}");
        }
    }

    private void ApplyMutation(
        NCPropertyRuntimeMutationResult result,
        NCPropertyAccessPolicyPrototype? accessPolicy,
        EntityUid? cardRecipient)
    {
        if (result.Property != null)
            Cache(result.Property);
        if (result.Result != NCPropertyMutationResult.Success || result.Grant == null || accessPolicy == null ||
            cardRecipient is not { } recipient || !Exists(recipient))
            return;

        var card = Spawn(accessPolicy.KeyCardPrototype, Transform(recipient).Coordinates);
        var credential = EnsureComp<NCPropertyKeyCardComponent>(card);
        credential.GrantId = result.Grant.GrantId;
        credential.PropertyId = result.Property!.Snapshot.PropertyId;
        credential.AccessRevision = result.Grant.AccessRevision;
        credential.Permissions = result.Grant.Permissions;
        credential.ValidUntil = result.Grant.ValidUntil;
        Dirty(card, credential);
        _metadata.SetEntityName(card, $"Property key-card {credential.PropertyId}");
    }

    private void Cache(NCPropertyAggregateRecord property)
    {
        _properties[property.Snapshot.PropertyId] = property;
        if (_locks.TryGetValue(property.Snapshot.PropertyId, out var locks))
        {
            foreach (var uid in locks.ToArray())
            {
                if (!TryComp(uid, out NCPropertyLockComponent? propertyLock))
                    continue;
                propertyLock.AccessRevision = property.Snapshot.AccessRevision;
                propertyLock.Enabled = property.Snapshot.Status is not (NCPropertyStatus.Unavailable or
                    NCPropertyStatus.Condemned or NCPropertyStatus.Destroyed);
                Dirty(uid, propertyLock);
            }
        }

        if (TryGetProcessingTime(property.Snapshot, out var processingAt))
            _rentQueue.Enqueue(new RentDueEntry(property.Snapshot.PropertyId, property.AggregateVersion), processingAt.Ticks);

        var ev = new NCPropertyChangedEvent(property.Snapshot.PropertyId);
        RaiseLocalEvent(ref ev);
    }

    private async Task ContinueRenovationAsync(PendingRenovation pending)
    {
        await _snapshots.DeleteSnapshotAsync(pending.Property.Snapshot);
        var request = new NCZoneDeploymentRequest(
            NCZoneDeploymentId.New(), pending.Property.Snapshot.PropertyId.ZoneId, pending.TemplateId,
            NCZoneDeploymentPurpose.ApartmentInterior, pending.ActorCharacterId, null);
        if (!_templates.TryRequestDeployment(request, out var deploymentId, out var error))
        {
            await RollbackRenovationAsync(pending.Property, pending.Rollback,
                $"new-interior-request-failed:{error}");
            return;
        }
        _pendingInteriors[deploymentId] = new PendingInterior(
            pending.Property.Snapshot.PropertyId, pending.TemplateId.Id, pending.Property.AggregateVersion,
            pending.Rollback);
    }

    private Task RollbackRenovationAsync(
        NCPropertyAggregateRecord property,
        RenovationRollback rollback,
        string reason) => RollbackRenovationAsync(property.Snapshot.PropertyId, property.AggregateVersion,
        rollback, reason);

    private async Task RollbackRenovationAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        RenovationRollback rollback,
        string reason)
    {
        var stable = $"{propertyId}.{expectedVersion}.{rollback.PreviousRenovationRevision}";
        var settlement = await _settlements.RollbackRenovationAsync(new NCRollbackPropertyRenovationRequest(
            NCPersistentIdentity.Command("housing", "renovation-rollback", stable),
            NCPersistentIdentity.Transaction("housing", "renovation-refund", stable), propertyId,
            expectedVersion, rollback.Actor, rollback.Price, rollback.PreviousTemplateId,
            rollback.PreviousTemplateRevision, rollback.PreviousInteriorState,
            rollback.PreviousRenovationRevision,
            NCPersistentIdentity.Correlation("housing", "renovation-rollback", stable),
            AuditSource: $"housing-renovation-rollback:{reason}"));
        if (settlement.Property != null)
            Cache(settlement.Property);
        if (settlement.Status is not (NCPropertySettlementStatus.Applied or NCPropertySettlementStatus.Duplicate))
            Log.Error($"Renovation rollback for {propertyId} failed: {settlement.Error}");
    }

    private void OnCanonicalPropertyPersisted(NCPropertyAggregatePersistedEvent args)
    {
        Cache(args.Property);
        if (args.Property.Snapshot.Status is NCPropertyStatus.Condemned or NCPropertyStatus.Destroyed)
            _ = ApplyLegalRuntimeStateAsync(args.Property);
    }

    private async Task ApplyLegalRuntimeStateAsync(NCPropertyAggregateRecord property)
    {
        var snapshot = property.Snapshot;
        if (snapshot.Status == NCPropertyStatus.Condemned && snapshot.CurrentTemplateId != null &&
            !await _snapshots.CapturePropertySnapshotAsync(snapshot))
        {
            Log.Error($"Condemned property {snapshot.PropertyId} was retained because its content capture failed.");
            return;
        }
        if (_runtimeInteriors.TryGetValue(snapshot.PropertyId, out var deploymentId) &&
            !_templates.TryCloseDeployment(deploymentId, out var error))
        {
            Log.Error($"Legal unload of property {snapshot.PropertyId} failed: {error}");
            return;
        }
        _runtimeInteriors.Remove(snapshot.PropertyId);
        if (snapshot.Status == NCPropertyStatus.Destroyed)
            await _snapshots.DeleteSnapshotAsync(snapshot);
    }

    private bool TryGetProcessingTime(NCPropertyAggregateSnapshot property, out NCActiveWorldTime processingAt)
    {
        processingAt = default;
        if (property.NextRentDueAt is not { } due ||
            !_prototypes.TryIndex<NCRentPolicyPrototype>(property.RentPolicyId, out var policy))
            return false;

        if (property.Status == NCPropertyStatus.Rented)
        {
            processingAt = due;
            return true;
        }
        if (property.Status == NCPropertyStatus.EvictionPending && property.EvictionAt is { } evictionAt)
        {
            processingAt = evictionAt;
            return true;
        }
        if (property.Status != NCPropertyStatus.Delinquent)
            return false;

        try
        {
            processingAt = due.Advance(policy.GracePeriod);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void OnLockStartup(Entity<NCPropertyLockComponent> entity, ref ComponentStartup args)
    {
        if (!entity.Comp.PropertyId.IsValid)
            return;
        if (!_locks.TryGetValue(entity.Comp.PropertyId, out var locks))
            _locks[entity.Comp.PropertyId] = locks = [];
        locks.Add(entity.Owner);
        ApplyPropertyLockState(entity);
    }

    private void OnLockShutdown(Entity<NCPropertyLockComponent> entity, ref ComponentShutdown args)
    {
        foreach (var (propertyId, locks) in _locks.ToArray())
        {
            locks.Remove(entity.Owner);
            if (locks.Count == 0)
                _locks.Remove(propertyId);
        }
    }

    private void ApplyPropertyLockState(Entity<NCPropertyLockComponent> entity)
    {
        if (!_properties.TryGetValue(entity.Comp.PropertyId, out var property))
            return;

        entity.Comp.AccessRevision = property.Snapshot.AccessRevision;
        entity.Comp.Enabled = property.Snapshot.Status is not (NCPropertyStatus.Unavailable or
            NCPropertyStatus.Condemned or NCPropertyStatus.Destroyed);
        Dirty(entity, entity.Comp);
    }

    private bool TryGetListing(NCApartmentId id, out NCPropertyListingPrototype listing)
    {
        foreach (var candidate in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
        {
            if (candidate.ApartmentId != id)
                continue;
            listing = candidate;
            return true;
        }

        listing = default!;
        return false;
    }

    private bool TryGetInteriorOffer(
        NCPropertyListingPrototype listing,
        ProtoId<NCZoneTemplatePrototype> templateId,
        out NCPropertyInteriorOffer offer,
        out NCZoneTemplatePrototype template,
        out NCRentPolicyPrototype rentPolicy)
    {
        offer = listing.Interiors.FirstOrDefault(value => value.Template == templateId)!;
        template = default!;
        rentPolicy = default!;
        if (offer == null ||
            !_prototypes.TryIndex(offer.Template, out NCZoneTemplatePrototype? resolvedTemplate) ||
            resolvedTemplate.TargetZoneId != listing.ApartmentId.ZoneId ||
            !_prototypes.TryIndex(offer.RentPolicy, out NCRentPolicyPrototype? resolvedPolicy) ||
            resolvedPolicy.RentAmount < 0 || resolvedPolicy.BillingInterval <= TimeSpan.Zero ||
            resolvedPolicy.GracePeriod < TimeSpan.Zero)
            return false;

        template = resolvedTemplate;
        rentPolicy = resolvedPolicy;
        return true;
    }

    private async Task<NCPropertyRuntimeMutationResult> EvictAsync(NCPropertyAggregateRecord current)
    {
        var tenant = current.Snapshot.Occupants.First(occupant =>
            occupant.Kind == NCPropertyOccupancyKind.Tenancy).Occupant;
        var refund = _prototypes.TryIndex<NCRentPolicyPrototype>(current.Snapshot.RentPolicyId, out var policy)
            ? policy.EvictionDepositRefundBasisPoints
            : 0;
        var stable = $"{current.Snapshot.PropertyId}.{current.AggregateVersion}";
        var settlement = await _settlements.ReleaseLeaseAsync(new NCReleasePropertyLeaseSettlementRequest(
            NCPersistentIdentity.Command("housing", "evict", stable),
            NCPersistentIdentity.Transaction("housing", "evict", stable), current.Snapshot.PropertyId,
            current.AggregateVersion, tenant, current.Snapshot.Owner, NCPropertyReleaseReason.Eviction, refund,
            NCPersistentIdentity.Correlation("housing", "evict", stable), current.Snapshot.LastAppliedEventId,
            "housing-nonpayment-eviction"));
        return FromSettlement(settlement, Guid.Empty);
    }

    private async Task<NCPropertyRuntimeMutationResult> UpdatePropertyAsync(
        NCPropertyAggregateRecord current,
        NCPropertyAggregateSnapshot proposed,
        NCPartyRef initiator,
        string eventType,
        string auditSource,
        NCPropertyAccessGrantSnapshot? grant = null)
    {
        var result = await _registry.UpdateAsync(new NCUpdatePropertyRequest(
            Guid.NewGuid(),
            current.AggregateVersion,
            proposed,
            eventType,
            initiator,
            Guid.NewGuid(),
            AuditSource: auditSource));
        return result.Status switch
        {
            NCPropertyAggregateMutationStatus.Applied or NCPropertyAggregateMutationStatus.Duplicate =>
                new NCPropertyRuntimeMutationResult(NCPropertyMutationResult.Success, result.Property, grant, null),
            NCPropertyAggregateMutationStatus.NotFound => Failed(NCPropertyMutationResult.NotFound, result.Property),
            NCPropertyAggregateMutationStatus.VersionConflict => Failed(NCPropertyMutationResult.Conflict, result.Property),
            _ => Failed(NCPropertyMutationResult.InvalidRequest, result.Property),
        };
    }

    private static NCPropertyAggregateSnapshot Vacate(NCPropertyAggregateSnapshot snapshot)
    {
        return snapshot with
        {
            Occupants = snapshot.Occupants.Where(value => value.Kind != NCPropertyOccupancyKind.Tenancy).ToArray(),
            AccessGrants = [],
            Status = NCPropertyStatus.Available,
            AcquiredAt = null,
            AccessRevision = checked(snapshot.AccessRevision + 1),
            RentPolicyId = string.Empty,
            CurrentTemplateId = null,
            CurrentTemplateRevision = null,
            ActiveDeploymentId = default,
            InteriorState = "empty",
            NextRentDueAt = null,
            DelinquentSince = null,
            EvictionAt = null,
        };
    }

    private static bool IsActiveTenant(
        NCPropertyAggregateSnapshot property,
        NCPartyRef party,
        NCActiveWorldTime now)
    {
        return property.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Tenancy &&
            value.Occupant == party && (value.ValidUntil == null || value.ValidUntil > now));
    }

    private static NCPropertyRuntimeMutationResult FromSettlement(
        NCPropertySettlementResult result,
        Guid grantId)
    {
        NCPropertyAccessGrantSnapshot? grant = null;
        if (result.Property != null && grantId != Guid.Empty)
            grant = result.Property.Snapshot.AccessGrants.SingleOrDefault(value => value.GrantId == grantId);

        var status = result.Status switch
        {
            NCPropertySettlementStatus.Applied or NCPropertySettlementStatus.Duplicate => NCPropertyMutationResult.Success,
            NCPropertySettlementStatus.NotFound => NCPropertyMutationResult.NotFound,
            NCPropertySettlementStatus.NotAvailable => NCPropertyMutationResult.NotAvailable,
            NCPropertySettlementStatus.NotAuthorized => NCPropertyMutationResult.NotAuthorized,
            NCPropertySettlementStatus.InsufficientFunds => NCPropertyMutationResult.InsufficientFunds,
            NCPropertySettlementStatus.VersionConflict => NCPropertyMutationResult.Conflict,
            _ => NCPropertyMutationResult.InvalidRequest,
        };
        return new NCPropertyRuntimeMutationResult(status, result.Property, grant, null);
    }

    private static bool HasPermission(
        NCPropertyAggregateSnapshot property,
        NCPartyRef party,
        NCPropertyPermission permission,
        NCActiveWorldTime now)
    {
        if (property.Owner == party)
            return true;
        return property.AccessGrants.Any(grant => grant.Subject == party &&
            grant.AccessRevision == property.AccessRevision && grant.Permissions.HasFlag(permission) &&
            (grant.ValidUntil == null || grant.ValidUntil > now));
    }

    private static NCPropertyRuntimeMutationResult FromMarketSettlement(
        NCPropertyMarketSettlementResult result,
        Guid grantId)
    {
        var grant = result.Property?.Snapshot.AccessGrants.SingleOrDefault(value => value.GrantId == grantId);
        var status = result.Status switch
        {
            NCPropertySettlementStatus.Applied or NCPropertySettlementStatus.Duplicate =>
                NCPropertyMutationResult.Success,
            NCPropertySettlementStatus.NotFound => NCPropertyMutationResult.NotFound,
            NCPropertySettlementStatus.NotAvailable => NCPropertyMutationResult.NotAvailable,
            NCPropertySettlementStatus.NotAuthorized => NCPropertyMutationResult.NotAuthorized,
            NCPropertySettlementStatus.InsufficientFunds => NCPropertyMutationResult.InsufficientFunds,
            NCPropertySettlementStatus.VersionConflict => NCPropertyMutationResult.Conflict,
            _ => NCPropertyMutationResult.InvalidRequest,
        };
        return new NCPropertyRuntimeMutationResult(status, result.Property, grant, null);
    }

    private static NCPropertyRuntimeMutationResult Failed(
        NCPropertyMutationResult result,
        NCPropertyAggregateRecord? property = null) => new(result, property, null, null);

    private readonly record struct PendingInterior(
        NCApartmentId PropertyId,
        string TemplateId,
        long ExpectedVersion,
        RenovationRollback? Rollback);

    private readonly record struct PendingRenovation(
        NCPropertyAggregateRecord Property,
        NCCharacterId ActorCharacterId,
        ProtoId<NCZoneTemplatePrototype> TemplateId,
        RenovationRollback Rollback);

    private readonly record struct RenovationRollback(
        NCPartyRef Actor,
        long Price,
        string? PreviousTemplateId,
        int? PreviousTemplateRevision,
        string PreviousInteriorState,
        int PreviousRenovationRevision);

    private readonly record struct RentDueEntry(NCApartmentId PropertyId, long AggregateVersion);
}

/// <summary>Raised whenever the authoritative property cache changes.</summary>
[ByRefEvent]
public readonly record struct NCPropertyChangedEvent(NCApartmentId PropertyId);
