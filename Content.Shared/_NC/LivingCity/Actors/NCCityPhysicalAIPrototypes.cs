// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Local observation throughput policy. It is separate from cognition and never creates persistent beliefs directly.
/// </summary>
[Prototype("ncCityLocalPerceptionProfile")]
public sealed partial class NCCityLocalPerceptionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public float Radius { get; private set; }

    [DataField(required: true)]
    public int MaximumObservedEntities { get; private set; }

    [DataField(required: true)]
    public int MaximumLineOfSightChecks { get; private set; }

    [DataField(required: true)]
    public TimeSpan EvaluationInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan WitnessedEventLifetime { get; private set; }

    /// <summary>
    /// Minimum cadence at which a continuing assault may wake the same victim again. The event remains one
    /// correlated memory, while fresh damage still gets a bounded chance to interrupt cognition.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan RepeatedIncidentWakeInterval { get; private set; }

    /// <summary>Maximum bounded history of ordinary perception changes retained for one physical actor.</summary>
    [DataField(required: true)]
    public int MaximumPerceptionDeltas { get; private set; }

    /// <summary>Active-world lifetime of an ordinary perception change supplied to GPT.</summary>
    [DataField(required: true)]
    public TimeSpan PerceptionDeltaLifetime { get; private set; }

    /// <summary>
    /// Minimum active-world interval between ordinary sensorium wakes. Changes inside the interval are coalesced
    /// into the next actor-scoped wake instead of producing one provider request per evaluation.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan PerceptionDeltaWakeCooldown { get; private set; }
}

[Prototype("ncCityLocalPerceptionPolicy")]
public sealed partial class NCCityLocalPerceptionPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaximumUpdatesPerTick { get; private set; }

    [DataField]
    public int MaximumIncidentCorrelations { get; private set; } = 1024;
}
