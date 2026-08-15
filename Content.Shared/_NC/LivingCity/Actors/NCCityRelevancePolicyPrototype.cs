// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven limits for player relevance, LOD clustering and physical-to-strategic hand-offs.
/// These radii do not replace Robust PVS or local pathfinding.
/// </summary>
[Prototype("ncCityRelevancePolicy")]
public sealed partial class NCCityRelevancePolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public TimeSpan EvaluationInterval { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public float ClusterMergeRadius { get; private set; } = 40f;

    [DataField]
    public float ClusterSplitRadius { get; private set; } = 52f;

    [DataField]
    public float DematerializationRadius { get; private set; } = 36f;

    [DataField]
    public TimeSpan MaterializationCooldown { get; private set; } = TimeSpan.FromSeconds(15);

    [DataField]
    public int GlobalSoftMaterializedBudget { get; private set; } = 100;

    [DataField]
    public int GlobalHardMaterializedBudget { get; private set; } = 150;

    [DataField]
    public int ClusterSoftMaterializedBudget { get; private set; } = 32;

    [DataField]
    public int MaximumAnchors { get; private set; } = 128;

    [DataField]
    public int CatalogPageSize { get; private set; } = 250;

    [DataField]
    public int MaximumCandidateInspectionsPerCluster { get; private set; } = 128;

    [DataField]
    public int MaximumMaterializationRequestsPerEvaluation { get; private set; } = 4;

    [DataField]
    public int MaximumDematerializationInspectionsPerEvaluation { get; private set; } = 64;

    [DataField]
    public int MaximumDematerializationRequestsPerEvaluation { get; private set; } = 2;

    [DataField]
    public int PlayerRelevancePriority { get; private set; } = 500;

    [DataField]
    public int MaximumQueuedDematerializations { get; private set; } = 256;

    [DataField]
    public int MaximumDematerializationDispatchesPerTick { get; private set; } = 2;

    [DataField]
    public int MaximumConcurrentDematerializations { get; private set; } = 4;

    [DataField]
    public int MaximumCompletedDematerializationHistory { get; private set; } = 1024;

    [DataField]
    public TimeSpan DematerializationRetryDelay { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan DematerializationMaximumWait { get; private set; } = TimeSpan.FromMinutes(2);
}
