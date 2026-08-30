// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Converts bounded business shortages into public commodity requests. It runs only on active-world time and only for
/// explicitly aggregate-safe commodities, so unique, story-significant and illegal assets never become background demand.
/// </summary>
public sealed partial class NCAggregateDemandSystem : EntitySystem
{
    private static readonly ProtoId<NCAggregateDemandPolicyPrototype> DefaultPolicy = new("Default");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCBusinessRegistrySystem _businesses = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;

    private bool _active;
    private bool _running;
    private int _generation;
    private NCActiveWorldTime _nextRun;
    private string? _businessCursor;
    private Task? _inFlight;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _running || !_clock.TryGetCurrentTime(out _, out var now) || now < _nextRun ||
            !TryPolicy(out var policy))
            return;
        _running = true;
        _nextRun = now.Advance(policy.Interval);
        _inFlight = RunAsync(policy, now, _generation);
        _ = FinishAsync(_inFlight);
    }

    private async Task RunAsync(NCAggregateDemandPolicyPrototype policy, NCActiveWorldTime now, int generation)
    {
        try
        {
            var page = await _businesses.GetPageAsync(_businessCursor, policy.MaximumBusinessesPerRun);
            if (!_active || generation != _generation || page.Error != null)
                return;
            _businessCursor = page.Businesses.Count < policy.MaximumBusinessesPerRun
                ? null
                : page.NextAfterAggregateId;
            var created = 0;
            foreach (var business in page.Businesses)
            {
                if (!_active || generation != _generation || created >= policy.MaximumRequestsPerRun)
                    return;
                if (business.Snapshot.Status is NCBusinessStatus.Suspended or NCBusinessStatus.Dissolved)
                    continue;
                var desiredStatus = await DesiredStatusAsync(business, policy, now);
                if (desiredStatus != business.Snapshot.Status)
                {
                    var stableStatus = $"{business.Snapshot.BusinessId}.{business.AggregateVersion}.{desiredStatus}";
                    await _businesses.ChangeStatusAsync(new NCChangeBusinessStatusRequest(
                        NCPersistentIdentity.Command("business-health", "status", stableStatus),
                        business.Snapshot.BusinessId,
                        business.AggregateVersion,
                        desiredStatus,
                        business.Snapshot.BusinessParty,
                        NCPersistentIdentity.Correlation("business-health", "status", stableStatus),
                        business.Snapshot.LastAppliedEventId,
                        "business-health"));
                }
                if (!business.Snapshot.Policy.AllowsAggregatedDemand)
                    continue;
                foreach (var demand in policy.CommodityDemands)
                {
                    if (created >= policy.MaximumRequestsPerRun ||
                        demand.BusinessKinds.Count > 0 && !demand.BusinessKinds.Contains(business.Snapshot.Kind) ||
                        !_prototypes.TryIndex(demand.Commodity, out var commodity) ||
                        !commodity.AllowsAggregatedDemand || commodity.LegalStatus == NCEconomicLegalStatus.Illegal)
                        continue;
                    var nodeId = demand.UseOutputStock
                        ? business.Snapshot.OutputStockNodeId
                        : business.Snapshot.InventoryStockNodeId;
                    var threshold = Threshold(demand.TargetQuantity,
                        business.Snapshot.Policy.RestockThresholdBasisPoints);
                    if (threshold <= 0 || await QuantityAsync(nodeId, business.Snapshot.BusinessParty, commodity.ID,
                            demand.MinimumQuality, now) >= threshold ||
                        await _market.FindOpenRequestAsync(
                            business.Snapshot.BusinessParty, NCMarketRequestKind.Commodity, commodity.ID) != null)
                        continue;
                    var bucket = now.Ticks / policy.Interval.Ticks;
                    var stable = $"aggregate-demand.{business.Snapshot.BusinessId}.{commodity.StableKey}.{bucket}";
                    var requestId = NCPersistentIdentity.MarketRequest(business.Snapshot.BusinessParty, stable);
                    var result = await _market.CreateRequestAsync(new NCCreateMarketRequestRequest(
                        NCPersistentIdentity.Command("aggregate-demand", "create", stable),
                        requestId,
                        NCMarketRequestKind.Commodity,
                        commodity.ID,
                        demand.RequestQuantity,
                        demand.MaximumUnitPrice,
                        demand.MinimumQuality,
                        business.Snapshot.BusinessParty,
                        NCMarketChannel.Agent,
                        new NCMarketAccessPolicy(NCMarketVisibility.Public, [], string.Empty, 0, 0),
                        business.Snapshot.OperatingZone,
                        policy.Interval + policy.Interval,
                        business.Snapshot.BusinessParty,
                        NCPersistentIdentity.Correlation("aggregate-demand", "create", stable),
                        business.Snapshot.LastAppliedEventId,
                        "aggregate-demand"));
                    if (result.Status is NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate)
                        created++;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Aggregate business demand failed: {exception}");
        }
    }

    private async Task<NCBusinessStatus> DesiredStatusAsync(
        NCBusinessRecord business,
        NCAggregateDemandPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        var account = await _ledger.GetAccountAsync(business.Snapshot.AccountId, true);
        if (account == null || account.Snapshot.Status != NCAccountStatus.Active ||
            account.Snapshot.Balance < business.Snapshot.Policy.MinimumCashReserve)
            return NCBusinessStatus.Insolvent;
        foreach (var demand in policy.CommodityDemands)
        {
            if (demand.BusinessKinds.Count > 0 && !demand.BusinessKinds.Contains(business.Snapshot.Kind) ||
                !_prototypes.TryIndex(demand.Commodity, out var commodity) || !commodity.AllowsAggregatedDemand)
                continue;
            var threshold = Threshold(demand.TargetQuantity, business.Snapshot.Policy.RestockThresholdBasisPoints);
            if (threshold <= 0)
                continue;
            var nodeId = demand.UseOutputStock
                ? business.Snapshot.OutputStockNodeId
                : business.Snapshot.InventoryStockNodeId;
            if (await QuantityAsync(nodeId, business.Snapshot.BusinessParty, commodity.ID,
                    demand.MinimumQuality, now) < threshold)
                return NCBusinessStatus.Shortage;
        }
        return NCBusinessStatus.Active;
    }

    private static long Threshold(long target, int basisPoints) =>
        checked(target * basisPoints / 10_000);

    private async Task<long> QuantityAsync(
        NCStockNodeId nodeId,
        Content.Shared._NC.Persistence.Identity.NCPartyRef owner,
        string commodityId,
        int minimumQuality,
        NCActiveWorldTime now)
    {
        var node = await _assets.GetStockNodeAsync(nodeId, true);
        if (node == null || node.Snapshot.Owner != owner)
            return 0;
        long quantity = 0;
        foreach (var batchId in node.Snapshot.CommodityBatchIds)
        {
            var batch = await _assets.GetCommodityBatchAsync(batchId, true);
            if (batch == null || batch.Snapshot.Owner != owner || batch.Snapshot.CommodityId != commodityId ||
                batch.Snapshot.Quality < minimumQuality || batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                batch.Snapshot.ExpiresAt is { } expiry && expiry <= now)
                continue;
            quantity = checked(quantity + batch.Snapshot.Quantity);
        }
        return quantity;
    }

    private async Task FinishAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            if (ReferenceEquals(_inFlight, task))
                _inFlight = null;
            _running = false;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        _generation++;
        _businessCursor = null;
        _nextRun = args.Time;
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight != null)
            args.AddPendingWrite(_inFlight);
    }

    private void Reset()
    {
        _active = false;
        _running = false;
        _generation++;
        _businessCursor = null;
        _inFlight = null;
    }

    private bool TryPolicy(out NCAggregateDemandPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && policy.Interval > TimeSpan.Zero &&
        policy.MaximumBusinessesPerRun is > 0 and <= 100 && policy.MaximumRequestsPerRun is > 0 and <= 100 &&
        policy.CommodityDemands.Count <= 64 && policy.CommodityDemands.All(demand =>
            demand.TargetQuantity > 0 && demand.RequestQuantity > 0 && demand.MaximumUnitPrice >= 0 &&
            demand.MinimumQuality is >= 0 and <= 1000 && demand.BusinessKinds.Count <= 8 &&
            demand.BusinessKinds.All(kind => Enum.IsDefined(kind)));
}
