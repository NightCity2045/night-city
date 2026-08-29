// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Employment.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared.Bed.Sleep;
using Content.Shared.Damage.Components;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Nutrition.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityRoutineDecisionSource : byte
{
    Routine,
    Commitment,
}

public readonly record struct NCCityRoutineDecision(
    NCCityRoutineDecisionSource Source,
    NCCityCommitmentId CommitmentId,
    string Activity,
    string Goal,
    NCZoneId TargetZone,
    NCCityTravelMode TravelMode,
    NCCityActorAvailability Availability,
    NCCityActorPresenceMode PresenceMode,
    NCActiveWorldTime NextUpdateAt);

public readonly record struct NCCityCivilTimeSnapshot(
    NCSessionEpoch Epoch,
    long Revision,
    long Day,
    TimeSpan TimeOfDay,
    TimeSpan CivilDayDuration,
    TimeSpan ActiveDayDuration,
    TimeSpan ManualAdvance);

/// <summary>
/// Deterministically evaluates cyclic routines and bounded actor commitments against active-world time.
/// It does not move actors or perform persistence; the schedule scheduler owns those side effects.
/// </summary>
public sealed partial class NCCityRoutineSystem : EntitySystem
{
    private const int MaximumRoutineEntries = 64;
    private const int MaximumShiftOffsets = 16;
    private const int MaximumTextLength = 128;
    private static readonly ProtoId<NCCityCivilClockPolicyPrototype> DefaultCivilClock = "NCDefaultCityCivilClock";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCLivelihoodRegistrySystem _livelihoods = default!;

    private NCSessionEpoch _epoch;
    private long _manualCivilAdvanceTicks;
    private long _civilRevision;
    private bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public bool TryGetCivilTime(out NCCityCivilTimeSnapshot snapshot, out string? error)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            snapshot = default;
            error = "civil-time-inactive";
            return false;
        }

        return TryBuildCivilTime(epoch, now, _manualCivilAdvanceTicks, _civilRevision, out snapshot, out error);
    }

    /// <summary>
    /// Advances only the round-local civil schedule phase. Active-world time and every non-routine deadline remain
    /// unchanged.
    /// </summary>
    public bool TryAdvanceCivilTime(
        TimeSpan advance,
        out NCCityCivilTimeSnapshot snapshot,
        out string? error)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            snapshot = default;
            error = "civil-time-inactive";
            return false;
        }

        if (!TryGetCivilClockPolicy(out var clockPolicy, out error))
        {
            snapshot = default;
            return false;
        }

        if (advance <= TimeSpan.Zero || advance > clockPolicy.MaximumManualAdvance)
        {
            snapshot = default;
            error = "civil-time-advance-out-of-range";
            return false;
        }

        try
        {
            var candidateTicks = checked(_manualCivilAdvanceTicks + advance.Ticks);
            var candidateRevision = checked(_civilRevision + 1);
            if (!TryBuildCivilTime(
                    epoch,
                    now,
                    candidateTicks,
                    candidateRevision,
                    out snapshot,
                    out error))
            {
                return false;
            }

            // Commit only after the complete snapshot has been calculated successfully. A failed overflow or
            // validation therefore cannot leave the civil phase half-advanced.
            _manualCivilAdvanceTicks = candidateTicks;
            _civilRevision = candidateRevision;
            return true;
        }
        catch (OverflowException)
        {
            snapshot = default;
            error = "civil-time-overflow";
            return false;
        }

    }

    public bool TryEvaluate(
        NCCityActorRecord actor,
        NCActiveWorldTime now,
        out NCCityRoutineDecision decision,
        out string? error)
    {
        decision = default;
        error = null;
        if (!now.IsValid || actor.Snapshot.Identity.ActorId.IsValid == false)
        {
            error = "Actor identity or active-world time is invalid.";
            return false;
        }

        if (!_prototypes.TryIndex(
                actor.Snapshot.Schedule.RoutineProfileId,
                out NCCityRoutineProfilePrototype? routine) ||
            routine == null ||
            !ValidateProfile(routine, out error))
        {
            error ??= $"Unknown routine profile {actor.Snapshot.Schedule.RoutineProfileId}.";
            return false;
        }

        if (!_prototypes.TryIndex(
                actor.Snapshot.Identity.ArchetypeId,
                out NCCityActorArchetypePrototype? archetype) ||
            archetype == null)
        {
            error = $"Unknown actor archetype {actor.Snapshot.Identity.ArchetypeId}.";
            return false;
        }

        if (!TryGetCivilClockPolicy(out var clockPolicy, out error))
            return false;

        if (routine.RestDays.Count > 0 && routine.CycleDuration != clockPolicy.CivilDayDuration)
        {
            error = $"Routine {routine.ID} uses rest days but its cycle is not one civil day.";
            return false;
        }

        var routineDecision = EvaluateRoutine(actor.Snapshot, routine, clockPolicy, archetype.TravelMode, now);
        if (TryBuildRecoveryDecision(actor.Snapshot, routine, now, routineDecision.NextUpdateAt,
                out var recoveryDecision))
        {
            if (!TryValidateTarget(routine.ID, recoveryDecision.TargetZone, out error))
                return false;
            decision = recoveryDecision;
            return true;
        }

        var commitments = actor.Snapshot.Schedule.Commitments ?? [];
        var activeCommitment = commitments
            .Where(commitment => commitment.StartsAt <= now && commitment.EndsAt > now)
            .OrderByDescending(static commitment => commitment.Priority)
            .ThenByDescending(static commitment => commitment.StartsAt)
            .ThenBy(static commitment => commitment.CommitmentId.Value)
            .FirstOrDefault();

        var nextBoundary = routineDecision.NextUpdateAt;
        foreach (var commitment in commitments)
        {
            if (commitment.StartsAt > now && commitment.StartsAt < nextBoundary)
                nextBoundary = commitment.StartsAt;
            if (commitment.EndsAt > now && commitment.EndsAt < nextBoundary)
                nextBoundary = commitment.EndsAt;
        }

        if (activeCommitment == null)
        {
            routineDecision = ApplyDailyConditions(actor.Snapshot, routine, now,
                routineDecision with { NextUpdateAt = nextBoundary });
            if (!TryValidateTarget(routine.ID, routineDecision.TargetZone, out error))
                return false;
            decision = routineDecision;
            return true;
        }

        if (!_zones.TryGetZone(activeCommitment.TargetZone, out _))
        {
            error = $"Commitment {activeCommitment.CommitmentId} references a missing target zone.";
            return false;
        }

        decision = new NCCityRoutineDecision(
            NCCityRoutineDecisionSource.Commitment,
            activeCommitment.CommitmentId,
            activeCommitment.Activity,
            activeCommitment.Goal,
            activeCommitment.TargetZone,
            archetype.TravelMode,
            activeCommitment.Availability,
            NCCityActorPresenceMode.Public,
            nextBoundary);
        return true;
    }

    public bool ValidateAll(List<string> errors)
    {
        errors.Clear();
        if (!TryGetCivilClockPolicy(out _, out var clockError))
            errors.Add($"{DefaultCivilClock}: {clockError}");
        foreach (var profile in _prototypes.EnumeratePrototypes<NCCityRoutineProfilePrototype>()
                     .OrderBy(static profile => profile.ID, StringComparer.Ordinal))
        {
            if (!ValidateProfile(profile, out var error))
                errors.Add($"{profile.ID}: {error}");
        }

        return errors.Count == 0;
    }

    public bool ValidateProfile(NCCityRoutineProfilePrototype profile, out string? error)
    {
        if (profile.CycleDuration <= TimeSpan.Zero)
        {
            error = "The routine cycle duration must be positive.";
            return false;
        }

        if (profile.ActorPhaseSpread < TimeSpan.Zero || profile.ActorPhaseSpread >= profile.CycleDuration)
        {
            error = "The actor phase spread must fit the routine cycle.";
            return false;
        }

        if (profile.ShiftOffsets.Count is < 1 or > MaximumShiftOffsets ||
            profile.ShiftOffsets.Any(offset => offset < TimeSpan.Zero || offset >= profile.CycleDuration) ||
            profile.ShiftOffsets.Distinct().Count() != profile.ShiftOffsets.Count)
        {
            error = $"A routine needs 1..{MaximumShiftOffsets} unique shift offsets inside its cycle.";
            return false;
        }

        error = null;
        if (profile.NeedEvaluationInterval <= TimeSpan.Zero ||
            profile.RecoveryEvaluationInterval <= TimeSpan.Zero ||
            !Enum.IsDefined(profile.MealThreshold) || !float.IsFinite(profile.FatigueStaminaRatio) ||
            profile.FatigueStaminaRatio is <= 0f or > 1f ||
            profile.RestDays.Any(day => !Enum.IsDefined(day)) ||
            profile.RestDays.Count != 0 && profile.RestDayEntries.Count == 0 ||
            !ValidateEntries(profile.Entries, profile.CycleDuration, out error) ||
            profile.RestDayEntries.Count > 0 &&
            !ValidateEntries(profile.RestDayEntries, profile.CycleDuration, out error))
        {
            error ??= "The routine need, rest-day, or entry policy is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateEntries(
        IReadOnlyList<NCCityRoutineEntry> entries,
        TimeSpan cycleDuration,
        out string? error)
    {
        if (entries.Count is < 1 or > MaximumRoutineEntries || entries[0].At != TimeSpan.Zero)
        {
            error = $"A routine needs 1..{MaximumRoutineEntries} entries and must begin at zero.";
            return false;
        }

        var previous = TimeSpan.MinValue;
        foreach (var entry in entries)
        {
            if (entry.At <= previous || entry.At < TimeSpan.Zero || entry.At >= cycleDuration ||
                string.IsNullOrWhiteSpace(entry.Activity) || entry.Activity.Length > MaximumTextLength ||
                entry.Goal == null || entry.Goal.Length > MaximumTextLength ||
                !Enum.IsDefined(entry.Target) || !Enum.IsDefined(entry.Availability) ||
                entry.TravelMode != NCCityTravelMode.None && !HasSingleTravelMode(entry.TravelMode))
            {
                error = "Entries must be ordered, inside the cycle, named, and contain valid enum values.";
                return false;
            }

            if (entry.Target == NCCityRoutineTarget.ExplicitZone)
            {
                if (entry.ExplicitZone is not { IsValid: true } explicitZone ||
                    !_zones.TryGetZone(explicitZone, out _))
                {
                    error = $"Entry at {entry.At} references a missing explicit zone.";
                    return false;
                }
            }
            else if (entry.ExplicitZone is { IsValid: true })
            {
                error = $"Entry at {entry.At} supplies explicitZone for target {entry.Target}.";
                return false;
            }

            previous = entry.At;
        }

        error = null;
        return true;
    }

    private NCCityRoutineDecision EvaluateRoutine(
        NCCityActorSnapshot actor,
        NCCityRoutineProfilePrototype routine,
        NCCityCivilClockPolicyPrototype clockPolicy,
        NCCityTravelMode defaultTravelMode,
        NCActiveWorldTime now)
    {
        var cycleTicks = routine.CycleDuration.Ticks;
        var scaledCivilTicks = ScaleTicksFloor(
            now.Ticks,
            clockPolicy.CivilDayDuration.Ticks,
            clockPolicy.ActiveDayDuration.Ticks);
        var actorPhaseOffset = GetActorPhaseOffset(
            actor.Identity.ActorId,
            routine.ActorPhaseSpread.Ticks,
            routine.ShiftOffsets);
        var calendarTicks = scaledCivilTicks + clockPolicy.InitialTimeOfDay.Ticks + _manualCivilAdvanceTicks;
        var actorCalendarTicks = calendarTicks + actorPhaseOffset;
        var positionTicks = PositiveModulo(
            actorCalendarTicks,
            cycleTicks);
        var weekday = (NCCityWeekday) PositiveModulo(
            FloorDiv(actorCalendarTicks, clockPolicy.CivilDayDuration.Ticks), 7);
        var entries = routine.RestDays.Contains(weekday) ? routine.RestDayEntries : routine.Entries;
        var activeIndex = 0;
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].At.Ticks > positionTicks)
                break;
            activeIndex = i;
        }

        var entry = entries[activeIndex];
        var ticksUntilNext = activeIndex + 1 < entries.Count
            ? entries[activeIndex + 1].At.Ticks - positionTicks
            : cycleTicks - positionTicks;
        if (routine.RestDays.Count > 0)
        {
            // Wake at the calendar-day boundary even when actor phase offset moved the next personal entry later.
            var untilNextDay = clockPolicy.CivilDayDuration.Ticks -
                               PositiveModulo(actorCalendarTicks, clockPolicy.CivilDayDuration.Ticks);
            ticksUntilNext = Math.Min(ticksUntilNext, untilNextDay);
        }
        var targetZone = entry.Target switch
        {
            NCCityRoutineTarget.Home => actor.Location.HomeZone,
            NCCityRoutineTarget.Work => actor.Location.WorkZone,
            NCCityRoutineTarget.Community => ResolveCommunityZone(actor),
            NCCityRoutineTarget.ExplicitZone => entry.ExplicitZone!.Value,
            _ => actor.Location.CurrentZone,
        };
        var travelMode = entry.TravelMode == NCCityTravelMode.None
            ? defaultTravelMode
            : entry.TravelMode;
        var presenceMode = entry.Target == NCCityRoutineTarget.Home
            ? NCCityActorPresenceMode.AbstractHome
            : NCCityActorPresenceMode.Public;

        // Routine entries are authored in civil time. Only their wait to the next boundary is converted back to
        // active-world time; unrelated gameplay deadlines keep their normal duration.
        var activeTicksUntilNext = ScaleTicksCeiling(
            ticksUntilNext,
            clockPolicy.ActiveDayDuration.Ticks,
            clockPolicy.CivilDayDuration.Ticks);
        return new NCCityRoutineDecision(
            NCCityRoutineDecisionSource.Routine,
            default,
            entry.Activity,
            entry.Goal,
            targetZone,
            travelMode,
            entry.Availability,
            presenceMode,
            now.Advance(TimeSpan.FromTicks(activeTicksUntilNext)));
    }

    private bool TryBuildRecoveryDecision(
        NCCityActorSnapshot actor,
        NCCityRoutineProfilePrototype routine,
        NCActiveWorldTime now,
        NCActiveWorldTime routineBoundary,
        out NCCityRoutineDecision decision)
    {
        var status = actor.Lifecycle.Status;
        if (status is not (NCCityActorStatus.Resting or NCCityActorStatus.Injured or
            NCCityActorStatus.Hospitalized or
            NCCityActorStatus.Unconscious))
        {
            decision = default;
            return false;
        }

        var remainPublic = status is NCCityActorStatus.Resting or NCCityActorStatus.Hospitalized or
            NCCityActorStatus.Unconscious;
        var target = remainPublic ? actor.Location.CurrentZone : actor.Location.HomeZone;
        decision = new NCCityRoutineDecision(
            NCCityRoutineDecisionSource.Routine,
            default,
            "recover",
            status == NCCityActorStatus.Injured ? "recover-at-home" : "await-medical-recovery",
            target,
            actor.Location.TravelMode,
            NCCityActorAvailability.Unavailable,
            remainPublic ? NCCityActorPresenceMode.Public : NCCityActorPresenceMode.AbstractHome,
            Earlier(routineBoundary, now.Advance(routine.RecoveryEvaluationInterval)));
        return true;
    }

    private NCCityRoutineDecision ApplyDailyConditions(
        NCCityActorSnapshot actor,
        NCCityRoutineProfilePrototype routine,
        NCActiveWorldTime now,
        NCCityRoutineDecision decision)
    {
        if (_runtime.TryGetEntity(actor.Identity.ActorId, out var entity) && Exists(entity))
        {
            decision = decision with
            {
                NextUpdateAt = Earlier(decision.NextUpdateAt, now.Advance(routine.NeedEvaluationInterval)),
            };
            if (HasComp<SleepingComponent>(entity) ||
                TryComp<StaminaComponent>(entity, out var stamina) && stamina.CritThreshold > 0f &&
                stamina.StaminaDamage / stamina.CritThreshold >= routine.FatigueStaminaRatio)
            {
                return decision with
                {
                    Activity = "rest",
                    Goal = "recover-from-fatigue",
                    TargetZone = actor.Location.CurrentZone,
                    Availability = NCCityActorAvailability.Busy,
                    PresenceMode = NCCityActorPresenceMode.Public,
                };
            }
            if (TryComp<HungerComponent>(entity, out var hunger) &&
                hunger.CurrentThreshold <= routine.MealThreshold)
            {
                return decision with
                {
                    Activity = "meal",
                    Goal = "answer-hunger",
                    TargetZone = actor.Location.CurrentZone,
                    Availability = NCCityActorAvailability.Busy,
                    PresenceMode = NCCityActorPresenceMode.Public,
                };
            }
        }

        var workBound = decision.TargetZone == actor.Location.WorkZone ||
            decision.Activity.Equals("prepare", StringComparison.OrdinalIgnoreCase) ||
            decision.Activity.Equals("commute", StringComparison.OrdinalIgnoreCase) ||
            decision.Activity.Equals("work", StringComparison.OrdinalIgnoreCase);
        if (!workBound || !IsUnemployed(actor))
            return decision;

        return decision with
        {
            Activity = "unemployed-free-time",
            Goal = "seek-local-opportunity",
            TargetZone = ResolveCommunityZone(actor),
            Availability = NCCityActorAvailability.Available,
            PresenceMode = NCCityActorPresenceMode.Public,
        };
    }

    private bool IsUnemployed(NCCityActorSnapshot actor)
    {
        if (_workforce.TryGetAssignment(actor.Identity.ActorId, out _, out _))
            return false;
        if (_runtime.TryGetEntity(actor.Identity.ActorId, out var entity) &&
            TryComp<NCCityLivelihoodComponent>(entity, out var component) &&
            _prototypes.TryIndex(component.Profile, out NCLivelihoodProfilePrototype? physicalProfile) &&
            physicalProfile != null)
        {
            return physicalProfile.Category == NCLivelihoodCategory.Unemployed;
        }
        if (_livelihoods.TryGetCached(actor.Identity.ActorId, out var livelihood) &&
            _prototypes.TryIndex(livelihood.Snapshot.ProfileId, out NCLivelihoodProfilePrototype? profile) &&
            profile != null)
        {
            return profile.Category == NCLivelihoodCategory.Unemployed;
        }
        return !actor.Location.WorkZone.IsValid;
    }

    private bool TryValidateTarget(string routineId, NCZoneId target, out string? error)
    {
        if (target.IsValid && _zones.TryGetZone(target, out _))
        {
            error = null;
            return true;
        }
        error = $"Routine {routineId} resolves to a missing actor target zone.";
        return false;
    }

    private NCZoneId ResolveCommunityZone(NCCityActorSnapshot actor)
    {
        var candidate = actor.Location.CurrentZone.IsValid
            ? actor.Location.CurrentZone
            : actor.Location.HomeZone;
        // Walk only the authored semantic hierarchy. A street/sector/district is a public-life scope; buildings and
        // apartments are deliberately escaped so a rest-day resident does not treat their workplace as community.
        for (var depth = 0; depth < 8 && candidate.IsValid && _zones.TryGetZone(candidate, out var zone); depth++)
        {
            if (zone.ContextSlot is NCZoneContextSlot.Street or NCZoneContextSlot.Sector or
                NCZoneContextSlot.District)
                return candidate;
            candidate = zone.Parent;
        }
        return actor.Location.CurrentZone.IsValid ? actor.Location.CurrentZone : actor.Location.HomeZone;
    }

    private static NCActiveWorldTime Earlier(NCActiveWorldTime left, NCActiveWorldTime right) =>
        left <= right ? left : right;

    private static long GetActorPhaseOffset(
        NCActorId actorId,
        long spreadTicks,
        IReadOnlyList<TimeSpan> shiftOffsets)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.Value.TryWriteBytes(bytes);
        var stableHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
                         BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var shift = shiftOffsets[(int) (stableHash % (ulong) shiftOffsets.Count)].Ticks;
        if (spreadTicks <= 0)
            return shift;
        var normalized = (decimal) stableHash / ulong.MaxValue;
        var phase = (long) decimal.Round(
            (normalized - 0.5m) * spreadTicks,
            0,
            MidpointRounding.ToZero);
        return shift + phase;
    }

    private static long ScaleTicksFloor(long valueTicks, long numeratorTicks, long denominatorTicks)
    {
        return checked((long) decimal.Floor((decimal) valueTicks * numeratorTicks / denominatorTicks));
    }

    private static long ScaleTicksCeiling(long valueTicks, long numeratorTicks, long denominatorTicks)
    {
        return Math.Max(1L,
            checked((long) decimal.Ceiling((decimal) valueTicks * numeratorTicks / denominatorTicks)));
    }

    private static long PositiveModulo(long value, long modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _epoch = args.Epoch;
        _manualCivilAdvanceTicks = 0;
        _civilRevision = 0;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            ResetCivilTime();
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_active && args.Epoch == _epoch)
            ResetCivilTime();
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            ResetCivilTime();
    }

    private bool TryBuildCivilTime(
        NCSessionEpoch epoch,
        NCActiveWorldTime now,
        long manualAdvanceTicks,
        long revision,
        out NCCityCivilTimeSnapshot snapshot,
        out string? error)
    {
        if (!TryGetCivilClockPolicy(out var clockPolicy, out error))
        {
            snapshot = default;
            return false;
        }

        try
        {
            var elapsedCivilTicks = ScaleTicksFloor(
                now.Ticks,
                clockPolicy.CivilDayDuration.Ticks,
                clockPolicy.ActiveDayDuration.Ticks);
            var totalCivilTicks = checked(
                elapsedCivilTicks + clockPolicy.InitialTimeOfDay.Ticks + manualAdvanceTicks);
            snapshot = new NCCityCivilTimeSnapshot(
                epoch,
                revision,
                totalCivilTicks / clockPolicy.CivilDayDuration.Ticks + 1,
                TimeSpan.FromTicks(totalCivilTicks % clockPolicy.CivilDayDuration.Ticks),
                clockPolicy.CivilDayDuration,
                clockPolicy.ActiveDayDuration,
                TimeSpan.FromTicks(manualAdvanceTicks));
            error = null;
            return true;
        }
        catch (OverflowException)
        {
            snapshot = default;
            error = "civil-time-overflow";
            return false;
        }
    }

    private bool TryGetCivilClockPolicy(
        out NCCityCivilClockPolicyPrototype clockPolicy,
        out string? error)
    {
        if (!_prototypes.TryIndex(DefaultCivilClock, out clockPolicy!) ||
            clockPolicy.CivilDayDuration <= TimeSpan.Zero ||
            clockPolicy.ActiveDayDuration <= TimeSpan.Zero ||
            clockPolicy.InitialTimeOfDay < TimeSpan.Zero ||
            clockPolicy.InitialTimeOfDay >= clockPolicy.CivilDayDuration ||
            clockPolicy.MaximumManualAdvance <= TimeSpan.Zero ||
            clockPolicy.MaximumManualAdvance > clockPolicy.CivilDayDuration)
        {
            error = "civil-time-profile-invalid";
            return false;
        }

        error = null;
        return true;
    }

    private void ResetCivilTime()
    {
        _active = false;
        _epoch = default;
        _manualCivilAdvanceTicks = 0;
        _civilRevision = 0;
    }
}
