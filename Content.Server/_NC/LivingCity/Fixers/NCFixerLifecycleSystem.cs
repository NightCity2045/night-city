// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Economy.Shop;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Advances deal expiry and physical Night Markets only while an active round clock exists. Recovery is paged and
/// queues stable aggregate IDs; no entity or database scan occurs in Update().
/// </summary>
public sealed partial class NCFixerLifecycleSystem : EntitySystem
{
    private const int RecoveryPageSize = 100;
    private const int MaximumRecoveredPerType = 10_000;
    private static readonly ProtoId<NCFixerPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNightMarketPolicyPrototype> DefaultNightMarketPolicy = "Default";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCFixerConsequenceSystem _consequences = default!;
    [Dependency] private NCFixerDealBoardSystem _dealBoard = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCEconomicShopSystem _shops = default!;

    private readonly PriorityQueue<DueWork, long> _due = new();
    private readonly HashSet<DueWork> _queued = [];
    private readonly HashSet<DueWork> _processing = [];
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<NCNightMarketId, NCZoneDeploymentId> _deployments = [];
    private readonly Dictionary<NCZoneDeploymentId, NCNightMarketId> _marketsByDeployment = [];
    private readonly HashSet<EntityUid> _deploymentEntities = [];
    private readonly Dictionary<NCNightMarketId, NCNightMarketRecord> _runtimeMarkets = [];
    private readonly Dictionary<NCNightMarketId, List<EntityUid>> _existingVenueEntities = [];
    private bool _active;
    private int _generation;

    public bool IsActive => _active;
    public int QueuedWorkCount => _queued.Count;
    public int InFlightCount => _inFlight.Count;
    public int RuntimeNightMarketCount => _runtimeMarkets.Count;
    public int ActiveDeploymentCount => _deployments.Count;
    public int ExistingVenueCount => _existingVenueEntities.Count;
    public long AttendanceProjectionFailures { get; private set; }
    public long ResourceValidationFailures { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCZoneDeploymentStateChangedEvent>(OnDeploymentChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out var policy))
            return;
        var count = 0;
        while (count < policy.MaximumLifecycleAdvancesPerTick &&
               _due.TryPeek(out var work, out var ticks) && ticks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(work);
            if (_processing.Add(work))
                Track(ProcessAsync(work, _generation));
            count++;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Clear();
        _active = true;
        _generation++;
        Track(RecoverAsync(_generation, args.Time));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void Clear()
    {
        ClearExistingVenues();
        _active = false;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
        _deployments.Clear();
        _marketsByDeployment.Clear();
        _runtimeMarkets.Clear();
    }

    private async Task RecoverAsync(int generation, NCActiveWorldTime now)
    {
        await RecoverDealsAsync(generation, now);
        await RecoverNightMarketsAsync(generation, now);
    }

    private async Task RecoverDealsAsync(int generation, NCActiveWorldTime now)
    {
        string? after = null;
        var recovered = 0;
        while (_active && generation == _generation && recovered < MaximumRecoveredPerType)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(NCFixerRegistrySystem.DealAggregateType,
                after, RecoveryPageSize);
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var deal = await _fixers.GetDealAsync(new NCFixerDealId(raw), true);
                if (deal?.Snapshot.Status is (NCFixerDealStatus.Offered or NCFixerDealStatus.Quoted) &&
                    deal.Snapshot.QuoteExpiresAt is { } quoteExpiry)
                    Enqueue(new DueWork(DueKind.Deal, raw), quoteExpiry);
                else if (deal?.Snapshot.Status == NCFixerDealStatus.Reserved &&
                    deal.Snapshot.ReservationExpiresAt is { } reservationExpiry)
                    Enqueue(new DueWork(DueKind.Deal, raw), reservationExpiry);
                else if (deal?.Snapshot.Status == NCFixerDealStatus.InProgress &&
                    deal.Snapshot.ExecutionDeadline is { } executionDeadline)
                    Enqueue(new DueWork(DueKind.Deal, raw), executionDeadline);
                else if (deal?.Snapshot.Status is NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Breached or
                    NCFixerDealStatus.Cancelled or NCFixerDealStatus.Expired)
                {
                    await _dealBoard.ReconcileDealResourcesAsync(deal);
                    if (deal.Snapshot.Status is NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Breached)
                        await _consequences.ApplyAsync(deal);
                }
                recovered++;
            }
            if (!_active || generation != _generation || page.Count < RecoveryPageSize)
                return;
            after = page[^1].AggregateId;
        }
    }

    private async Task RecoverNightMarketsAsync(int generation, NCActiveWorldTime now)
    {
        string? after = null;
        var recovered = 0;
        while (_active && generation == _generation && recovered < MaximumRecoveredPerType)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(
                NCFixerRegistrySystem.NightMarketAggregateType, after, RecoveryPageSize);
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var market = await _fixers.GetNightMarketAsync(new NCNightMarketId(raw), true);
                if (market != null)
                {
                    _runtimeMarkets[market.Snapshot.NightMarketId] = market;
                    QueueNightMarket(market, now);
                }
                recovered++;
            }
            if (!_active || generation != _generation || page.Count < RecoveryPageSize)
                return;
            after = page[^1].AggregateId;
        }
    }

    private void QueueNightMarket(NCNightMarketRecord record, NCActiveWorldTime now)
    {
        var value = record.Snapshot;
        switch (value.Status)
        {
            case NCNightMarketStatus.Scheduled:
                Enqueue(new DueWork(DueKind.NightMarket, value.NightMarketId.Value), value.AssemblyAt);
                break;
            case NCNightMarketStatus.Assembling:
                EnsureDeployment(record);
                if (string.IsNullOrWhiteSpace(value.TemplateId))
                    Track(EnsureExistingVenueAsync(record));
                Enqueue(new DueWork(DueKind.NightMarket, value.NightMarketId.Value), value.OpensAt);
                break;
            case NCNightMarketStatus.Open:
                if (string.IsNullOrWhiteSpace(value.TemplateId))
                    Track(EnsureExistingVenueAsync(record));
                Enqueue(new DueWork(DueKind.NightMarket, value.NightMarketId.Value), value.ClosesAt);
                break;
            case NCNightMarketStatus.Closing:
                if (!_deployments.ContainsKey(value.NightMarketId))
                    Enqueue(new DueWork(DueKind.NightMarket, value.NightMarketId.Value), now);
                break;
        }
    }

    private async Task ProcessAsync(DueWork work, int generation)
    {
        try
        {
            if (!_active || generation != _generation)
                return;
            if (work.Kind == DueKind.Deal)
                await ExpireDealAsync(new NCFixerDealId(work.Id));
            else
                await AdvanceNightMarketAsync(new NCNightMarketId(work.Id));
        }
        finally
        {
            _processing.Remove(work);
        }
    }

    private async Task ExpireDealAsync(NCFixerDealId id)
    {
        var deal = await _fixers.GetDealAsync(id, true);
        if (deal == null || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var due = deal.Snapshot.Status switch
        {
            NCFixerDealStatus.Offered or NCFixerDealStatus.Quoted => deal.Snapshot.QuoteExpiresAt,
            NCFixerDealStatus.Reserved => deal.Snapshot.ReservationExpiresAt,
            NCFixerDealStatus.InProgress => deal.Snapshot.ExecutionDeadline,
            _ => null,
        };
        if (due is not { } expiry || expiry > now)
        {
            if (due is { } future)
                Enqueue(new DueWork(DueKind.Deal, id.Value), future);
            return;
        }
        var stable = $"{id}.{deal.AggregateVersion}";
        var target = deal.Snapshot.Status == NCFixerDealStatus.InProgress
            ? NCFixerDealStatus.Breached
            : NCFixerDealStatus.Expired;
        await _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
            NCPersistentIdentity.Command("fixer", "expire-deal", stable), id, deal.AggregateVersion,
            target, deal.Snapshot.Fixer,
            NCPersistentIdentity.Correlation("fixer", "expire-deal", stable),
            deal.Snapshot.LastAppliedEventId, "fixer-active-time-expiry",
            target == NCFixerDealStatus.Breached
                ? NCFixerBreachReason.ExecutionDeadlineMissed
                : NCFixerBreachReason.None));
    }

    private async Task AdvanceNightMarketAsync(NCNightMarketId id)
    {
        var market = await _fixers.GetNightMarketAsync(id, true);
        if (market == null || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var target = market.Snapshot.Status switch
        {
            NCNightMarketStatus.Scheduled when now >= market.Snapshot.AssemblyAt => NCNightMarketStatus.Assembling,
            NCNightMarketStatus.Assembling when now >= market.Snapshot.OpensAt => NCNightMarketStatus.Open,
            NCNightMarketStatus.Open when now >= market.Snapshot.ClosesAt => NCNightMarketStatus.Closing,
            NCNightMarketStatus.Closing when !_deployments.ContainsKey(id) => NCNightMarketStatus.Completed,
            _ => (NCNightMarketStatus?) null,
        };
        if (target == null)
        {
            QueueNightMarket(market, now);
            return;
        }

        if (target == NCNightMarketStatus.Open)
        {
            var attendance = await PrepareAttendanceForOpeningAsync(market);
            market = attendance.Market;
            if (attendance.Error != null)
            {
                RecordResourceFailure(id, attendance.Error);
                await CancelNightMarketAsync(market, "missing-physical-attendance");
                return;
            }
        }
        var worldEvent = NCPersistentIdentity.Command("night-market", "world-event",
            $"{id}.{market.AggregateVersion}.{target}");
        var result = await _fixers.AdvanceNightMarketAsync(id, market.AggregateVersion, target.Value,
            worldEvent, "night-market-lifecycle");
        if (result.NightMarket == null)
        {
            if (target is NCNightMarketStatus.Assembling or NCNightMarketStatus.Open && result.Error != null)
            {
                RecordResourceFailure(id, result.Error);
                var current = await _fixers.GetNightMarketAsync(id, true);
                if (current != null)
                    await CancelNightMarketAsync(current, "resource-validation-failed");
            }
            return;
        }
        _runtimeMarkets[id] = result.NightMarket;
        if (target == NCNightMarketStatus.Assembling)
        {
            await ArrangeAttendanceAsync(result.NightMarket);
            EnsureDeployment(result.NightMarket);
            if (string.IsNullOrWhiteSpace(result.NightMarket.Snapshot.TemplateId))
                await EnsureExistingVenueAsync(result.NightMarket);
        }
        else if (target == NCNightMarketStatus.Open)
            SetVenueOpen(id, true);
        else if (target == NCNightMarketStatus.Closing)
        {
            var closing = await RecordDeparturesAsync(result.NightMarket);
            _runtimeMarkets[id] = closing;
            SetVenueOpen(id, false);
            CloseExistingVenue(id);
            await ReleaseAttendanceAsync(closing);
            if (_deployments.TryGetValue(id, out var deploymentId) &&
                !_templates.TryCloseDeployment(deploymentId, out var error))
                Log.Error($"Night Market {id} could not close its venue: {error}");
        }
        QueueNightMarket(result.NightMarket, now);
    }

    private async Task<NCNightMarketRecord> RecordDeparturesAsync(NCNightMarketRecord market)
    {
        var current = market;
        foreach (var vendor in market.Snapshot.Vendors.Where(value =>
                     value.ArrivedAt.HasValue && !value.DepartedAt.HasValue))
        {
            var result = await _fixers.SetVendorAttendanceAsync(current.Snapshot.NightMarketId,
                current.AggregateVersion, vendor.Vendor, false);
            if (result.NightMarket == null)
            {
                AttendanceProjectionFailures++;
                break;
            }
            current = result.NightMarket;
        }
        return current;
    }

    private void EnsureDeployment(NCNightMarketRecord market)
    {
        if (_deployments.ContainsKey(market.Snapshot.NightMarketId))
            return;
        // An empty template uses an already-authored shop or warehouse; no duplicate map package is deployed.
        if (string.IsNullOrWhiteSpace(market.Snapshot.TemplateId))
            return;
        var templateId = new ProtoId<NCZoneTemplatePrototype>(market.Snapshot.TemplateId);
        if (!_prototypes.HasIndex(templateId))
        {
            Log.Error($"Night Market {market.Snapshot.NightMarketId} template is unavailable.");
            return;
        }
        var request = new NCZoneDeploymentRequest(NCZoneDeploymentId.New(), market.Snapshot.VenueZone, templateId,
            NCZoneDeploymentPurpose.NightMarket, null, null);
        if (!_templates.TryRequestDeployment(request, out var deploymentId, out var error))
        {
            Log.Error($"Night Market {market.Snapshot.NightMarketId} deployment failed to start: {error}");
            return;
        }
        _deployments[market.Snapshot.NightMarketId] = deploymentId;
        _marketsByDeployment[deploymentId] = market.Snapshot.NightMarketId;
    }

    private void OnDeploymentChanged(ref NCZoneDeploymentStateChangedEvent args)
    {
        if (!_marketsByDeployment.TryGetValue(args.DeploymentId, out var marketId))
            return;
        if (args.NewState == NCZoneDeploymentState.Active)
        {
            BindVenueControllers(marketId, args.DeploymentId);
            return;
        }
        if (args.NewState is not (NCZoneDeploymentState.Completed or NCZoneDeploymentState.FailedClean))
            return;
        _marketsByDeployment.Remove(args.DeploymentId);
        _deployments.Remove(marketId);
        if (_clock.TryGetCurrentTime(out _, out var now))
            Enqueue(new DueWork(DueKind.NightMarket, marketId.Value), now);
    }

    private void BindVenueControllers(NCNightMarketId marketId, NCZoneDeploymentId deploymentId)
    {
        if (!_templates.CollectDeploymentEntities(deploymentId, _deploymentEntities))
            return;
        foreach (var uid in _deploymentEntities)
        {
            if (!TryComp(uid, out NCNightMarketVenueComponent? venue))
                continue;
            venue.NightMarketId = marketId;
            venue.Open = false;
            Dirty(uid, venue);
        }
        if (!_runtimeMarkets.TryGetValue(marketId, out var market))
            return;
        var stalls = _deploymentEntities.Where(uid => HasComp<NCNightMarketVendorStallComponent>(uid))
            .OrderBy(uid => uid.Id).ToArray();
        var vendors = market.Snapshot.Vendors.OrderBy(value => value.Vendor.ToString(), StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < Math.Min(stalls.Length, vendors.Length); index++)
        {
            var stall = Comp<NCNightMarketVendorStallComponent>(stalls[index]);
            stall.NightMarketId = marketId;
            stall.Vendor = vendors[index].Vendor;
            Dirty(stalls[index], stall);
        }
    }

    private void SetVenueOpen(NCNightMarketId marketId, bool open)
    {
        if (_existingVenueEntities.TryGetValue(marketId, out var existing))
        {
            foreach (var uid in existing)
                _shops.SetExternalStockEnabled(uid, open);
        }
        if (!_deployments.TryGetValue(marketId, out var deploymentId) ||
            !_templates.CollectDeploymentEntities(deploymentId, _deploymentEntities))
            return;
        foreach (var uid in _deploymentEntities)
        {
            if (!TryComp(uid, out NCNightMarketVenueComponent? venue))
                continue;
            venue.Open = open;
            Dirty(uid, venue);
        }
    }

    private async Task ArrangeAttendanceAsync(NCNightMarketRecord market)
    {
        if (!_prototypes.TryIndex(DefaultNightMarketPolicy, out var policy))
            return;
        var participants = market.Snapshot.Vendors.Select(static value =>
                value.Attendant.IsValid ? value.Attendant : value.Vendor)
            .Append(market.Snapshot.Organizer)
            .Append(market.Snapshot.SecurityProvider)
            .Distinct()
            .ToArray();
        foreach (var participant in participants)
        {
            if (!participant.TryGetActor(out var actorId) ||
                await TryAddAttendanceCommitmentAsync(market, actorId, policy.AttendancePriority))
                continue;
            AttendanceProjectionFailures++;
            LastError = $"Night Market {market.Snapshot.NightMarketId} could not route actor {actorId}.";
        }
    }

    /// <summary>
    /// An authored Shop/Warehouse affordance is a placement anchor only. Runtime stalls are bound to each vendor's
    /// existing stock node and are removed at close; no per-map Night Market routes or duplicate stock are authored.
    /// </summary>
    private async Task EnsureExistingVenueAsync(NCNightMarketRecord market)
    {
        var marketId = market.Snapshot.NightMarketId;
        if (_existingVenueEntities.ContainsKey(marketId) ||
            !_prototypes.TryIndex(DefaultNightMarketPolicy, out NCNightMarketPolicyPrototype? policy))
            return;
        NCCityAffordanceInfo? anchor = null;
        foreach (var kind in policy.AllowedVenueAffordances)
        {
            if (_affordances.TrySelect(kind, market.Snapshot.VenueZone,
                    unchecked((uint) marketId.Value.GetHashCode()), out var selected))
            {
                anchor = selected;
                break;
            }
        }
        if (anchor is not { } venue || !_coordinates.TryGetMap(venue.Target.NetworkId, venue.Target.Z, out var map))
        {
            RecordResourceFailure(marketId, "The authored Night Market anchor is not loaded.");
            return;
        }

        var spawned = new List<EntityUid>(market.Snapshot.Vendors.Count);
        var vendors = market.Snapshot.Vendors.OrderBy(value => value.Vendor.ToString(), StringComparer.Ordinal).ToArray();
        for (var index = 0; index < vendors.Length; index++)
        {
            var offset = new System.Numerics.Vector2(index % 4 * 1.5f, index / 4 * 1.5f);
            var stall = SpawnAtPosition("NCNightMarketVendorStall",
                new EntityCoordinates(map, venue.Target.Position + offset));
            var component = Comp<NCNightMarketVendorStallComponent>(stall);
            component.NightMarketId = marketId;
            component.Vendor = vendors[index].Vendor;
            Dirty(stall, component);
            if (!await _shops.BindExternalStockAsync(stall, vendors[index].Vendor, vendors[index].StockNodeId,
                    market.Snapshot.Status == NCNightMarketStatus.Open))
            {
                QueueDel(stall);
                continue;
            }
            spawned.Add(stall);
        }
        if (spawned.Count == 0)
        {
            RecordResourceFailure(marketId, "No Night Market stall could bind its real vendor stock.");
            return;
        }
        _existingVenueEntities[marketId] = spawned;
    }

    private void CloseExistingVenue(NCNightMarketId marketId)
    {
        if (!_existingVenueEntities.Remove(marketId, out var entities))
            return;
        foreach (var uid in entities)
        {
            if (Exists(uid))
                QueueDel(uid);
        }
    }

    private void ClearExistingVenues()
    {
        foreach (var marketId in _existingVenueEntities.Keys.ToArray())
            CloseExistingVenue(marketId);
    }

    private async Task<bool> TryAddAttendanceCommitmentAsync(NCNightMarketRecord market, NCActorId actorId,
        int priority)
    {
        var commitmentId = NCPersistentIdentity.NightMarketCommitment(market.Snapshot.NightMarketId, actorId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var actor = await _actors.GetAsync(actorId, true);
            if (actor == null)
                return false;
            if ((actor.Snapshot.Schedule.Commitments ?? []).Any(value => value.CommitmentId == commitmentId))
                return true;
            var stable = $"{market.Snapshot.NightMarketId}.{actorId}.{attempt}";
            var result = await _actors.AddCommitmentAsync(new NCCityActorAddCommitmentRequest(
                NCPersistentIdentity.Command("night-market", "add-attendance", stable), actorId,
                actor.AggregateVersion,
                new NCCityActorCommitment(commitmentId, "night-market", "attend-night-market",
                    market.Snapshot.VenueZone, market.Snapshot.AssemblyAt, market.Snapshot.ClosesAt,
                    NCCityActorAvailability.Busy, priority),
                NCPersistentIdentity.Correlation("night-market", "attendance", stable),
                market.Snapshot.LastAppliedEventId, "night-market-attendance"));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return true;
            if (result.Status != NCCityActorMutationStatus.VersionConflict)
                return false;
        }
        return false;
    }

    private async Task ReleaseAttendanceAsync(NCNightMarketRecord market)
    {
        var participants = market.Snapshot.Vendors.Select(static value =>
                value.Attendant.IsValid ? value.Attendant : value.Vendor)
            .Append(market.Snapshot.Organizer)
            .Append(market.Snapshot.SecurityProvider)
            .Distinct();
        foreach (var participant in participants)
        {
            if (!participant.TryGetActor(out var actorId))
                continue;
            var commitmentId = NCPersistentIdentity.NightMarketCommitment(market.Snapshot.NightMarketId, actorId);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var actor = await _actors.GetAsync(actorId, true);
                if (actor == null || (actor.Snapshot.Schedule.Commitments ?? []).All(value =>
                        value.CommitmentId != commitmentId))
                    break;
                var stable = $"{market.Snapshot.NightMarketId}.{actorId}.{attempt}";
                var result = await _actors.RemoveCommitmentAsync(new NCCityActorRemoveCommitmentRequest(
                    NCPersistentIdentity.Command("night-market", "remove-attendance", stable),
                    actorId, actor.AggregateVersion, commitmentId,
                    NCPersistentIdentity.Correlation("night-market", "remove-attendance", stable),
                    market.Snapshot.LastAppliedEventId, "night-market-closeout"));
                if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                    break;
                if (result.Status != NCCityActorMutationStatus.VersionConflict)
                {
                    AttendanceProjectionFailures++;
                    break;
                }
            }
        }
    }

    private async Task<AttendanceResult> PrepareAttendanceForOpeningAsync(NCNightMarketRecord market)
    {
        if (!await IsAtVenueAsync(market.Snapshot.Organizer, market.Snapshot.VenueZone) ||
            !await IsAtVenueAsync(market.Snapshot.SecurityProvider, market.Snapshot.VenueZone))
            return new AttendanceResult(market, "The organizer or security provider did not reach the venue.");

        var current = market;
        var arrived = 0;
        foreach (var vendor in market.Snapshot.Vendors)
        {
            var attendant = vendor.Attendant.IsValid ? vendor.Attendant : vendor.Vendor;
            if (!await IsAtVenueAsync(attendant, market.Snapshot.VenueZone))
                continue;
            arrived++;
            if (vendor.ArrivedAt != null)
                continue;
            var result = await _fixers.SetVendorAttendanceAsync(current.Snapshot.NightMarketId,
                current.AggregateVersion, vendor.Vendor, true);
            if (result.NightMarket == null)
                return new AttendanceResult(current, result.Error ?? "Vendor attendance could not persist.");
            current = result.NightMarket;
            _runtimeMarkets[current.Snapshot.NightMarketId] = current;
        }
        return arrived > 0
            ? new AttendanceResult(current, null)
            : new AttendanceResult(current, "No vendor reached the Night Market venue.");
    }

    private async Task<bool> IsAtVenueAsync(NCPartyRef party, Content.Shared._NC.City.Zones.NCZoneId venue)
    {
        if (!party.TryGetActor(out var actorId))
            return false;
        var actor = await _actors.GetAsync(actorId, true);
        return actor?.Snapshot.Location.CurrentZone == venue;
    }

    private async Task CancelNightMarketAsync(NCNightMarketRecord market, string reason)
    {
        if (market.Snapshot.Status is not (NCNightMarketStatus.Scheduled or NCNightMarketStatus.Assembling))
            return;
        var worldEvent = NCPersistentIdentity.Command("night-market", "cancel-event",
            $"{market.Snapshot.NightMarketId}.{market.AggregateVersion}.{reason}");
        var result = await _fixers.AdvanceNightMarketAsync(market.Snapshot.NightMarketId,
            market.AggregateVersion, NCNightMarketStatus.Cancelled, worldEvent, $"night-market-{reason}");
        if (result.NightMarket != null)
        {
            _runtimeMarkets[market.Snapshot.NightMarketId] = result.NightMarket;
            SetVenueOpen(market.Snapshot.NightMarketId, false);
            CloseExistingVenue(market.Snapshot.NightMarketId);
            await ReleaseAttendanceAsync(result.NightMarket);
        }
        if (_deployments.TryGetValue(market.Snapshot.NightMarketId, out var deploymentId) &&
            !_templates.TryCloseDeployment(deploymentId, out var error))
            RecordResourceFailure(market.Snapshot.NightMarketId, error ?? "Venue deployment could not close.");
    }

    private void RecordResourceFailure(NCNightMarketId marketId, string error)
    {
        ResourceValidationFailures++;
        LastError = $"Night Market {marketId}: {error}";
        Log.Error(LastError);
    }

    private void Enqueue(DueWork work, NCActiveWorldTime dueAt)
    {
        if (_active && _queued.Add(work))
            _due.Enqueue(work, dueAt.Ticks);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            Log.Error($"Fixer lifecycle operation failed: {exception}");
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private enum DueKind : byte { Deal, NightMarket }
    private readonly record struct DueWork(DueKind Kind, Guid Id);
    private readonly record struct AttendanceResult(NCNightMarketRecord Market, string? Error);
}
