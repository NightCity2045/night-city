// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Cyclic, active-world-time routine. The cycle advances only while a round is running.
/// The first entry must start at zero; each entry remains active until the next boundary.
/// </summary>
[Prototype("ncCityRoutineProfile")]
public sealed partial class NCCityRoutineProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public TimeSpan CycleDuration { get; private set; }

    /// <summary>
    /// Amount of active-world time needed to complete one full routine cycle.
    /// This keeps civil time scaling local to schedules instead of accelerating medical or persistence deadlines.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan ActiveCycleDuration { get; private set; }

    /// <summary>
    /// Civil-time phase at active-world time zero. It lets a fresh city session begin during an active part of the day.
    /// </summary>
    [DataField]
    public TimeSpan CyclePhaseOffset { get; private set; }

    /// <summary>
    /// Total deterministic phase range distributed around <see cref="CyclePhaseOffset"/> by stable actor identity.
    /// </summary>
    [DataField]
    public TimeSpan ActorPhaseSpread { get; private set; }

    /// <summary>
    /// Largest civil-time jump accepted from one administrative operation.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan MaximumManualAdvance { get; private set; }

    [DataField(required: true)]
    public List<NCCityRoutineEntry> Entries { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCCityRoutineEntry
{
    [DataField(required: true)]
    public TimeSpan At { get; private set; }

    [DataField(required: true)]
    public string Activity { get; private set; } = string.Empty;

    [DataField]
    public string Goal { get; private set; } = string.Empty;

    [DataField]
    public NCCityRoutineTarget Target { get; private set; } = NCCityRoutineTarget.Stay;

    [DataField(customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId ExplicitZone { get; private set; }

    [DataField]
    public NCCityActorAvailability Availability { get; private set; } = NCCityActorAvailability.Available;

    /// <summary>
    /// None keeps the actor archetype's travel mode.
    /// </summary>
    [DataField]
    public NCCityTravelMode TravelMode { get; private set; }
}

/// <summary>
/// Data-driven queue and retry limits for the persistent schedule scheduler.
/// </summary>
[Prototype("ncCitySchedulePolicy")]
public sealed partial class NCCitySchedulePolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int SnapshotPageSize { get; private set; } = 250;

    [DataField]
    public int MaximumDispatchesPerTick { get; private set; } = 4;

    [DataField]
    public int MaximumConcurrentMutations { get; private set; } = 8;

    [DataField]
    public TimeSpan MutationRetryDelay { get; private set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reconciles persisted schedules with the current routine policy at the beginning of every active-world session.
    /// Work is still admitted through the normal bounded scheduler queue.
    /// </summary>
    [DataField]
    public bool ReevaluateOnSessionStart { get; private set; } = true;
}
