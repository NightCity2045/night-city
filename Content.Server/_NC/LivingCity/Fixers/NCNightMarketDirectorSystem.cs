// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Schedules at most one automatic Night Market per active session from an existing Fixer network, a real authored
/// Shop/Warehouse anchor, and finite persistent vendor stock. Evaluation is event-driven, never an Update scan.
/// </summary>
public sealed partial class NCNightMarketDirectorSystem : EntitySystem
{
    private static readonly ProtoId<NCNightMarketPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;

    private readonly HashSet<Task> _inFlight = [];
    private bool _active;
    private bool _evaluating;
    private bool _scheduled;
    private int _generation;

    public bool ScheduledThisSession => _scheduled;
    public int InFlightCount => _inFlight.Count;
    public long Evaluations { get; private set; }
    public int EligibleDemandCount { get; private set; }
    public long ScheduleFailures { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
        SubscribeLocalEvent<NCFixerAggregateChangedEvent>(OnFixerChanged);
        SubscribeLocalEvent<NCMarketAggregateChangedEvent>(OnMarketChanged);
        SubscribeLocalEvent<NCCityAffordanceCatalogChangedEvent>(OnAffordancesChanged);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        _active = true;
        _generation++;
        QueueEvaluation();
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void OnFixerChanged(NCFixerAggregateChangedEvent args)
    {
        if (args.AggregateType == NCFixerRegistrySystem.NetworkAggregateType)
            QueueEvaluation();
    }

    private void OnMarketChanged(NCMarketAggregateChangedEvent args)
    {
        if (args.AggregateType == NCMarketRegistrySystem.RequestAggregateType)
            QueueEvaluation();
    }

    private void OnAffordancesChanged(ref NCCityAffordanceCatalogChangedEvent args) => QueueEvaluation();

    private void QueueEvaluation()
    {
        if (!_active || _scheduled || _evaluating)
            return;
        _evaluating = true;
        Track(EvaluateAsync(_generation));
    }

    private async Task EvaluateAsync(int generation)
    {
        try
        {
            Evaluations++;
            if (!_active || generation != _generation ||
                !_clock.TryGetCurrentTime(out var epoch, out var now) ||
                !_prototypes.TryIndex(DefaultPolicy, out NCNightMarketPolicyPrototype? policy) ||
                !policy.AutomaticSchedulingEnabled || policy.AutomaticVendorItems.Count == 0)
                return;

            var configured = policy.AutomaticVendorItems.ToHashSet();
            var requestedItems = (await _market.GetOpenRequestsAsync(64))
                .Where(value => value.Snapshot.Kind == NCMarketRequestKind.UniqueItem)
                .Select(value => new ProtoId<NCItemDefinitionPrototype>(value.Snapshot.DefinitionId))
                .Where(configured.Contains)
                .Distinct()
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            EligibleDemandCount = requestedItems.Length;
            if (requestedItems.Length == 0)
            {
                LastError = null;
                return;
            }

            var networks = _fixers.GetCachedNetworks().OrderBy(value => value.Snapshot.NetworkId.Value).ToArray();
            if (networks.Length == 0)
            {
                LastError = "Eligible item demand exists, but no Fixer network is available.";
                return;
            }
            var venueFound = false;
            foreach (var network in networks)
            {
                if (!_active || generation != _generation)
                    return;
                if (!TrySelectVenue(policy, network.Snapshot.NetworkId.Value, out var venue))
                    continue;
                venueFound = true;
                var stock = await EnsureFiniteVendorStockAsync(network, requestedItems);
                if (stock == null)
                    continue;

                var assembly = now.Advance(policy.MinimumAssemblyLead);
                var opens = now.Advance(policy.AutomaticOpenDelay > policy.MinimumAssemblyLead
                    ? policy.AutomaticOpenDelay
                    : policy.MinimumAssemblyLead + TimeSpan.FromMinutes(1));
                var closes = opens.Advance(policy.AutomaticDuration);
                var stable = $"{epoch}.{network.Snapshot.NetworkId}";
                var marketId = new NCNightMarketId(
                    NCPersistentIdentity.Command("night-market-director", "market-id", stable));
                var vendor = new NCNightMarketVendor(network.Snapshot.Fixer, stock.Snapshot.StockNodeId,
                    network.Snapshot.NetworkId, true, null, null, network.Snapshot.Fixer);
                var result = await _fixers.ScheduleNightMarketAsync(new NCScheduleNightMarketRequest(
                    NCPersistentIdentity.Command("night-market-director", "schedule", stable),
                    marketId,
                    string.Empty,
                    venue.ZoneId,
                    network.Snapshot.Fixer,
                    network.Snapshot.Fixer,
                    [vendor],
                    assembly,
                    opens,
                    closes,
                    NCPersistentIdentity.Correlation("night-market-director", "schedule", stable),
                    network.Snapshot.LastAppliedEventId,
                    "automatic-night-market"));
                if (result.NightMarket == null)
                {
                    RecordFailure(result.Error ?? "The automatic Night Market could not be scheduled.");
                    continue;
                }
                _scheduled = true;
                LastError = null;
                return;
            }
            LastError = venueFound
                ? "Eligible item demand exists, but no finite vendor stock could be prepared."
                : "Eligible item demand exists, but no authored Shop/Warehouse venue is available.";
        }
        finally
        {
            _evaluating = false;
        }
    }

    private async Task<NCStockNodeRecord?> EnsureFiniteVendorStockAsync(
        NCFixerNetworkRecord network,
        IReadOnlyList<ProtoId<NCItemDefinitionPrototype>> requestedItems)
    {
        var fixer = network.Snapshot.Fixer;
        var nodeId = NCPersistentIdentity.StockNode(fixer, "night-market-vendor");
        var node = await _assets.GetStockNodeAsync(nodeId, true);
        if (node == null)
        {
            var stable = network.Snapshot.NetworkId.ToString();
            var created = await _assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
                NCPersistentIdentity.Command("night-market-director", "stock", stable),
                nodeId,
                $"night-market-vendor.{network.Snapshot.NetworkId}",
                NCStockNodeKind.Shop,
                fixer,
                $"night-market.vendor.{network.Snapshot.NetworkId}",
                true,
                true,
                fixer,
                NCPersistentIdentity.Correlation("night-market-director", "stock", stable),
                network.Snapshot.LastAppliedEventId,
                "automatic-night-market"));
            node = created.StockNode;
        }
        if (node == null || node.Snapshot.Owner != fixer)
            return null;

        for (var index = 0; index < requestedItems.Count; index++)
        {
            var definition = requestedItems[index];
            var stable = $"{network.Snapshot.NetworkId}.{definition.Id}.{index}";
            var source = NCPersistentIdentity.Command("night-market-director", "initial-source", stable);
            node = await _assets.GetStockNodeAsync(nodeId, true);
            if (node == null)
                return null;
            await _assets.CreateItemInstanceAsync(new NCCreateItemInstanceRequest(
                NCPersistentIdentity.Command("night-market-director", "initial-item", stable),
                new NCItemId(NCPersistentIdentity.Command("night-market-director", "item-id", stable)),
                definition,
                $"NM-{definition.Id}-{index + 1:D2}",
                null,
                fixer,
                nodeId,
                node.AggregateVersion,
                "night-market.initial-finite-stock",
                fixer,
                [source],
                fixer,
                NCPersistentIdentity.Correlation("night-market-director", "initial-item", stable),
                network.Snapshot.LastAppliedEventId,
                "automatic-night-market"));
        }

        node = await _assets.GetStockNodeAsync(nodeId, true);
        return node != null && node.Snapshot.ItemIds.Count + node.Snapshot.CommodityBatchIds.Count > 0
            ? node
            : null;
    }

    private bool TrySelectVenue(NCNightMarketPolicyPrototype policy, Guid seedSource,
        out NCCityAffordanceInfo venue)
    {
        var seed = unchecked((uint) seedSource.GetHashCode());
        foreach (var kind in policy.AllowedVenueAffordances)
        {
            if (_affordances.TrySelect(kind, default, seed, out venue))
                return true;
        }
        venue = default;
        return false;
    }

    private void RecordFailure(string error)
    {
        ScheduleFailures++;
        LastError = error;
        Log.Error($"Night Market director: {error}");
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
            RecordFailure(exception.Message);
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private void Reset()
    {
        _active = false;
        _generation++;
        _evaluating = false;
        _scheduled = false;
        EligibleDemandCount = 0;
    }
}
