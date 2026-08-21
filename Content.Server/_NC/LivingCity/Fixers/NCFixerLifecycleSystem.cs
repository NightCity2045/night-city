// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.Persistence.Time;
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

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;

    private readonly PriorityQueue<DueWork, long> _due = new();
    private readonly HashSet<DueWork> _queued = [];
    private readonly HashSet<DueWork> _processing = [];
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<NCNightMarketId, NCZoneDeploymentId> _deployments = [];
    private readonly Dictionary<NCZoneDeploymentId, NCNightMarketId> _marketsByDeployment = [];
    private readonly HashSet<EntityUid> _deploymentEntities = [];
    private readonly Dictionary<NCNightMarketId, NCNightMarketRecord> _runtimeMarkets = [];
    private bool _active;
    private int _generation;

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
            !_prototypes.TryIndex<NCFixerPolicyPrototype>("Default", out var policy))
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
                Enqueue(new DueWork(DueKind.NightMarket, value.NightMarketId.Value), value.OpensAt);
                break;
            case NCNightMarketStatus.Open:
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
            _ => null,
        };
        if (due is not { } expiry || expiry > now)
        {
            if (due is { } future)
                Enqueue(new DueWork(DueKind.Deal, id.Value), future);
            return;
        }
        var stable = $"{id}.{deal.AggregateVersion}";
        await _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
            NCPersistentIdentity.Command("fixer", "expire-deal", stable), id, deal.AggregateVersion,
            NCFixerDealStatus.Expired, deal.Snapshot.Fixer,
            NCPersistentIdentity.Correlation("fixer", "expire-deal", stable),
            deal.Snapshot.LastAppliedEventId, "fixer-active-time-expiry"));
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
        var worldEvent = NCPersistentIdentity.Command("night-market", "world-event",
            $"{id}.{market.AggregateVersion}.{target}");
        var result = await _fixers.AdvanceNightMarketAsync(id, market.AggregateVersion, target.Value,
            worldEvent, "night-market-lifecycle");
        if (result.NightMarket == null)
            return;
        _runtimeMarkets[id] = result.NightMarket;
        if (target == NCNightMarketStatus.Assembling)
            EnsureDeployment(result.NightMarket);
        else if (target == NCNightMarketStatus.Open)
            SetVenueOpen(id, true);
        else if (target == NCNightMarketStatus.Closing)
        {
            SetVenueOpen(id, false);
            if (_deployments.TryGetValue(id, out var deploymentId) &&
                !_templates.TryCloseDeployment(deploymentId, out var error))
                Log.Error($"Night Market {id} could not close its venue: {error}");
        }
        QueueNightMarket(result.NightMarket, now);
    }

    private void EnsureDeployment(NCNightMarketRecord market)
    {
        if (_deployments.ContainsKey(market.Snapshot.NightMarketId))
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
}
