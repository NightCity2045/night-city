// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Bounds physical actor startup work without limiting how many persistent NPCs may exist in the round.
/// </summary>
[Prototype("ncPhysicalActorLifecyclePolicy")]
public sealed partial class NCPhysicalActorLifecyclePolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int SnapshotPageSize { get; private set; } = 128;

    [DataField]
    public int MaximumDispatchesPerTick { get; private set; } = 4;

    [DataField]
    public int MaximumConcurrentLoads { get; private set; } = 8;

    [DataField]
    public int MaximumCandidateChecks { get; private set; } = 128;

    [DataField]
    public int MaximumCompletedRequestHistory { get; private set; } = 2048;

    [DataField]
    public float ActorOccupancyExclusionRadius { get; private set; } = 0.6f;

    [DataField]
    public float AdminPreferredSpawnRadius { get; private set; } = 28f;

    [DataField]
    public TimeSpan RetryDelay { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan CheckpointDebounce { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan CheckpointMaximumDeferral { get; private set; } = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan CheckpointRetryDelay { get; private set; } = TimeSpan.FromSeconds(5);

    [DataField]
    public int MaximumCheckpointDispatchesPerTick { get; private set; } = 4;
}
