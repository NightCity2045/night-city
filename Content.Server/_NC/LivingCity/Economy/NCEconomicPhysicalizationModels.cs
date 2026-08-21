// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

public sealed record NCEconomicPhysicalItemPlan(
    NCItemInstanceRecord Item,
    EntProtoId EntityPrototype,
    NCCityInventoryItemCategory Categories,
    NCEconomicAssetDomain Domain,
    bool InstalledInActor,
    IReadOnlyList<NCItemInstanceRecord> InstalledParts);

public sealed record NCEconomicPhysicalCommodityPlan(
    NCCommodityBatchRecord Batch,
    EntProtoId EntityPrototype,
    int UnitsPerEntity,
    string SolutionId,
    string? ReagentId);

public sealed record NCActorInventoryMaterializationPlan(
    NCActorId ActorId,
    NCStockNodeRecord StockNode,
    IReadOnlyList<NCEconomicPhysicalItemPlan> Items,
    IReadOnlyList<NCEconomicPhysicalCommodityPlan> Commodities,
    int PhysicalEntityCount);

public sealed record NCEconomicMaterializationPlanResult(
    NCActorInventoryMaterializationPlan? Plan,
    string? Error)
{
    public bool IsSuccess => Plan != null && Error == null;
}

public sealed record NCEconomicMaterializationLeaseView(
    NCActorId ActorId,
    NCStockNodeId StockNodeId,
    EntityUid ActorEntity,
    int UniqueEntityCount,
    int CommodityEntityCount);
