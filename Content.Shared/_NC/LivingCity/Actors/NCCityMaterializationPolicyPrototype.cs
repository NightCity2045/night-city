// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Operational bounds for active-round actor rehydration. Visibility and occupancy radii are conservative spawn
/// safety rules, not replacements for Robust PVS or local pathfinding.
/// </summary>
[Prototype("ncCityMaterializationPolicy")]
public sealed partial class NCCityMaterializationPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int MaximumQueuedRequests { get; private set; } = 1024;

    [DataField]
    public int MaximumDispatchesPerTick { get; private set; } = 2;

    [DataField]
    public int MaximumConcurrentLoads { get; private set; } = 4;

    [DataField]
    public int MaximumCandidateChecks { get; private set; } = 32;

    [DataField]
    public int MaximumCompletedRequestHistory { get; private set; } = 2048;

    [DataField]
    public int MaximumPriority { get; private set; } = 1000;

    [DataField]
    public float PlayerVisibilityExclusionRadius { get; private set; } = 24f;

    /// <summary>
    /// Automatic player-relevance spawns must remain inside this radius of a player or enabled camera anchor.
    /// Keeping it at or below the dematerialization radius prevents a spawn/despawn feedback loop.
    /// </summary>
    [DataField]
    public float PlayerRelevanceMaximumRadius { get; private set; } = 36f;

    [DataField]
    public float ActorOccupancyExclusionRadius { get; private set; } = 1f;

    [DataField]
    public TimeSpan RetryDelay { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan DefaultMaximumWait { get; private set; } = TimeSpan.FromSeconds(30);

    [DataField]
    public TimeSpan MaximumWait { get; private set; } = TimeSpan.FromMinutes(5);
}

