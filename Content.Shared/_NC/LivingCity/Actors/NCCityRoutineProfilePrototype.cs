// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared.Nutrition.Components;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

public enum NCCityWeekday : byte
{
    Monday,
    Tuesday,
    Wednesday,
    Thursday,
    Friday,
    Saturday,
    Sunday,
}

/// <summary>
/// Defines the city-facing clock independently from monotonic active-world time and actor routines.
/// </summary>
[Prototype("ncCityCivilClockPolicy")]
public sealed partial class NCCityCivilClockPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public TimeSpan CivilDayDuration { get; private set; }

    [DataField(required: true)]
    public TimeSpan ActiveDayDuration { get; private set; }

    [DataField]
    public TimeSpan InitialTimeOfDay { get; private set; }

    [DataField]
    public TimeSpan MaximumManualAdvance { get; private set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Cyclic routine evaluated against the shared civil clock. The cycle advances only while a round is running.
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
    /// Total deterministic phase range distributed around civil time by stable actor identity.
    /// </summary>
    [DataField]
    public TimeSpan ActorPhaseSpread { get; private set; }

    /// <summary>Deterministic shift offsets; one is selected per actor before applying the smaller phase spread.</summary>
    [DataField]
    public List<TimeSpan> ShiftOffsets { get; private set; } = new() { TimeSpan.Zero };

    [DataField(required: true)]
    public List<NCCityRoutineEntry> Entries { get; private set; } = [];

    [DataField]
    public HashSet<NCCityWeekday> RestDays { get; private set; } = [];

    [DataField]
    public List<NCCityRoutineEntry> RestDayEntries { get; private set; } = [];

    /// <summary>Only materialized Humans are sampled at this active-world interval for vanilla needs.</summary>
    [DataField]
    public TimeSpan NeedEvaluationInterval { get; private set; } = TimeSpan.FromMinutes(1);

    [DataField]
    public TimeSpan RecoveryEvaluationInterval { get; private set; } = TimeSpan.FromMinutes(1);

    [DataField]
    public HungerThreshold MealThreshold { get; private set; } = HungerThreshold.Peckish;

    [DataField]
    public float FatigueStaminaRatio { get; private set; } = 0.6f;
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
    public NCZoneId? ExplicitZone { get; private set; }

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

    [DataField]
    public int MaximumMutationAttempts { get; private set; } = 5;

    /// <summary>
    /// Deterministically spreads overdue recovery work so a round start never wakes the whole city at once.
    /// </summary>
    [DataField]
    public TimeSpan InitialEvaluationStagger { get; private set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Reconciles persisted schedules with the current routine policy at the beginning of every active-world session.
    /// Work is still admitted through the normal bounded scheduler queue.
    /// </summary>
    [DataField]
    public bool ReevaluateOnSessionStart { get; private set; } = true;
}
