// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Small round-scoped exclusion index shared by the physicalizer and persistent economy mutations. It contains no
/// asset data and prevents abstract writes against a stock node while its ECS projection is live.
/// </summary>
public sealed partial class NCEconomicMaterializationLeaseIndexSystem : EntitySystem
{
    private readonly Dictionary<NCStockNodeId, NCActorId> _actorsByStockNode = [];
    private readonly Dictionary<NCActorId, (NCStockNodeId StockNodeId, EntityUid Entity)> _stockByActor = [];
    private readonly Dictionary<NCStockNodeId, Guid> _externalByStockNode = [];
    private readonly Dictionary<Guid, (NCStockNodeId StockNodeId, EntityUid Entity)> _stockByExternalLease = [];

    public int Count => _stockByActor.Count + _stockByExternalLease.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    public bool IsLeased(NCStockNodeId stockNodeId) => _actorsByStockNode.ContainsKey(stockNodeId) ||
        _externalByStockNode.ContainsKey(stockNodeId);

    public bool TryAcquire(NCActorId actorId, NCStockNodeId stockNodeId, EntityUid entity)
    {
        if (!actorId.IsValid || !stockNodeId.IsValid || !entity.IsValid() ||
            _stockByActor.ContainsKey(actorId) || IsLeased(stockNodeId))
        {
            return false;
        }
        _stockByActor.Add(actorId, (stockNodeId, entity));
        _actorsByStockNode.Add(stockNodeId, actorId);
        return true;
    }

    public bool TryRelease(NCActorId actorId, EntityUid entity)
    {
        if (!_stockByActor.TryGetValue(actorId, out var lease) || lease.Entity != entity)
            return false;
        _stockByActor.Remove(actorId);
        _actorsByStockNode.Remove(lease.StockNodeId);
        return true;
    }

    /// <summary>
    /// Acquires the same stock exclusion for a non-actor projection such as a physical shipment parcel.
    /// </summary>
    public bool TryAcquireExternal(Guid leaseId, NCStockNodeId stockNodeId, EntityUid entity)
    {
        if (leaseId == Guid.Empty || !stockNodeId.IsValid || !entity.IsValid() ||
            _stockByExternalLease.ContainsKey(leaseId) || IsLeased(stockNodeId))
            return false;
        _stockByExternalLease.Add(leaseId, (stockNodeId, entity));
        _externalByStockNode.Add(stockNodeId, leaseId);
        return true;
    }

    public bool TryReleaseExternal(Guid leaseId, EntityUid entity)
    {
        if (!_stockByExternalLease.TryGetValue(leaseId, out var lease) || lease.Entity != entity)
            return false;
        _stockByExternalLease.Remove(leaseId);
        _externalByStockNode.Remove(lease.StockNodeId);
        return true;
    }

    private void Clear()
    {
        _stockByActor.Clear();
        _actorsByStockNode.Clear();
        _externalByStockNode.Clear();
        _stockByExternalLease.Clear();
    }
}
