// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Actor-specific capabilities passed to the unchanged Robust pathfinding and steering pipeline.
/// </summary>
[Prototype("ncCityPhysicalNavigationProfile")]
public sealed partial class NCCityPhysicalNavigationProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool InteractWithDoors { get; private set; } = true;

    [DataField]
    public bool CanClimb { get; private set; }

    [DataField]
    public bool CanPry { get; private set; }

    [DataField]
    public bool CanSmash { get; private set; }
}

/// <summary>
/// Active-round throughput policy for physical intent loading, waypoint execution and safe fallback behavior.
/// </summary>
[Prototype("ncCityPhysicalExecutionPolicy")]
public sealed partial class NCCityPhysicalExecutionPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Diagnostic pressure threshold only. Reaching it never rejects an actor or drops an intent.
    /// </summary>
    [DataField(required: true)]
    public int MaximumQueuedIntentLoads { get; private set; }

    [DataField(required: true)]
    public int MaximumIntentLoadsPerTick { get; private set; }

    [DataField(required: true)]
    public int MaximumConcurrentIntentLoads { get; private set; }

    [DataField(required: true)]
    public int MaximumExecutionUpdatesPerTick { get; private set; }

    [DataField(required: true)]
    public int MaximumConcurrentMutations { get; private set; }

    [DataField(required: true)]
    public int MaximumMutationAttempts { get; private set; }

    [DataField(required: true)]
    public int MaximumWaypoints { get; private set; }

    [DataField(required: true)]
    public int MaximumImmediateWaypointCompletions { get; private set; }

    [DataField(required: true)]
    public int MaximumReplans { get; private set; }

    [DataField(required: true)]
    public int MaximumTraceEntries { get; private set; }

    [DataField(required: true)]
    public TimeSpan EvaluationInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan IntentLoadRetryDelay { get; private set; }

    [DataField(required: true)]
    public TimeSpan WaypointTimeout { get; private set; }

    [DataField(required: true)]
    public TimeSpan PortalTraversalTimeout { get; private set; }

    [DataField(required: true)]
    public TimeSpan PortalInteractionRetryInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan ReplanBackoff { get; private set; }

    [DataField(required: true)]
    public TimeSpan MinimumEquivalentReplanInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan TopologyChangeCoalesceDelay { get; private set; }

    [DataField(required: true)]
    public TimeSpan BlockedRetryInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan MutationRetryDelay { get; private set; }

    [DataField(required: true)]
    public TimeSpan HtnResumeCooldown { get; private set; }

    [DataField(required: true)]
    public float WaypointReachRange { get; private set; }
}
