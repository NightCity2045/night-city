// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven bounds and classification thresholds for actor health and inventory projections.
/// </summary>
[Prototype("ncCityActorSummaryPolicy")]
public sealed partial class NCCityActorSummaryPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public float ImmobileMovementThreshold { get; private set; } = 0.1f;

    [DataField]
    public float ImpairedMovementThreshold { get; private set; } = 0.75f;

    [DataField]
    public float ActiveBleedingRateThreshold { get; private set; } = 0.001f;

    [DataField]
    public float FailedCyberwareIntegrityRatio { get; private set; } = 0.1f;

    [DataField]
    public float DegradedCyberwareIntegrityRatio { get; private set; } = 0.75f;

    [DataField]
    public int MaximumInventoryItems { get; private set; } = 512;

    [DataField]
    public int MaximumContainerDepth { get; private set; } = 8;

    [DataField]
    public int MaximumCommodityBundles { get; private set; } = 128;
}
