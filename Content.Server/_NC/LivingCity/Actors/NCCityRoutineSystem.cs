// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
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
    private const int MaximumTextLength = 128;
    private static readonly ProtoId<NCCityRoutineProfilePrototype> DefaultCivilRoutine = "NCDefaultCivilianRoutine";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private NCSessionEpoch _epoch;
    private long _manualCivilAdvanceTicks;
    private bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
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

        return TryBuildCivilTime(epoch, now, out snapshot, out error);
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

        if (!TryGetDefaultCivilRoutine(out var routine, out error))
        {
            snapshot = default;
            return false;
        }

        if (advance <= TimeSpan.Zero || advance > routine.MaximumManualAdvance)
        {
            snapshot = default;
            error = "civil-time-advance-out-of-range";
            return false;
        }

        try
        {
            _manualCivilAdvanceTicks = checked(_manualCivilAdvanceTicks + advance.Ticks);
        }
        catch (OverflowException)
        {
            snapshot = default;
            error = "civil-time-overflow";
            return false;
        }

        return TryBuildCivilTime(epoch, now, out snapshot, out error);
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

        var routineDecision = EvaluateRoutine(actor.Snapshot, routine, archetype.TravelMode, now);
        if (!routineDecision.TargetZone.IsValid || !_zones.TryGetZone(routineDecision.TargetZone, out _))
        {
            error = $"Routine {routine.ID} resolves to a missing actor target zone.";
            return false;
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
            decision = routineDecision with { NextUpdateAt = nextBoundary };
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
        if (profile.CycleDuration <= TimeSpan.Zero || profile.ActiveCycleDuration <= TimeSpan.Zero)
        {
            error = "Civil and active-world cycle durations must be positive.";
            return false;
        }

        if (profile.CyclePhaseOffset < TimeSpan.Zero || profile.CyclePhaseOffset >= profile.CycleDuration ||
            profile.ActorPhaseSpread < TimeSpan.Zero || profile.ActorPhaseSpread >= profile.CycleDuration ||
            profile.MaximumManualAdvance <= TimeSpan.Zero || profile.MaximumManualAdvance > profile.CycleDuration)
        {
            error = "Cycle phase offset and actor spread must fit the cycle, and the maximum manual advance must be positive and no longer than one cycle.";
            return false;
        }

        if (profile.Entries.Count is < 1 or > MaximumRoutineEntries ||
            profile.Entries[0].At != TimeSpan.Zero)
        {
            error = $"A routine needs 1..{MaximumRoutineEntries} entries and must begin at zero.";
            return false;
        }

        var previous = TimeSpan.MinValue;
        foreach (var entry in profile.Entries)
        {
            if (entry.At <= previous || entry.At < TimeSpan.Zero || entry.At >= profile.CycleDuration ||
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
        NCCityTravelMode defaultTravelMode,
        NCActiveWorldTime now)
    {
        var cycleTicks = routine.CycleDuration.Ticks;
        var activeCycleTicks = routine.ActiveCycleDuration.Ticks;
        var activePositionTicks = now.Ticks % activeCycleTicks;
        var scaledPositionTicks = ScaleTicksFloor(activePositionTicks, cycleTicks, activeCycleTicks);
        var actorPhaseOffset = GetActorPhaseOffset(
            actor.Identity.ActorId,
            routine.ActorPhaseSpread.Ticks);
        var positionTicks = PositiveModulo(
            scaledPositionTicks + routine.CyclePhaseOffset.Ticks + actorPhaseOffset + _manualCivilAdvanceTicks,
            cycleTicks);
        var activeIndex = 0;
        for (var i = 1; i < routine.Entries.Count; i++)
        {
            if (routine.Entries[i].At.Ticks > positionTicks)
                break;
            activeIndex = i;
        }

        var entry = routine.Entries[activeIndex];
        var ticksUntilNext = activeIndex + 1 < routine.Entries.Count
            ? routine.Entries[activeIndex + 1].At.Ticks - positionTicks
            : cycleTicks - positionTicks;
        var targetZone = entry.Target switch
        {
            NCCityRoutineTarget.Home => actor.Location.HomeZone,
            NCCityRoutineTarget.Work => actor.Location.WorkZone,
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
        var activeTicksUntilNext = ScaleTicksCeiling(ticksUntilNext, activeCycleTicks, cycleTicks);
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

    private static long GetActorPhaseOffset(NCActorId actorId, long spreadTicks)
    {
        if (spreadTicks <= 0)
            return 0;

        Span<byte> bytes = stackalloc byte[16];
        actorId.Value.TryWriteBytes(bytes);
        var stableHash = BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^
                         BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var normalized = (decimal) stableHash / ulong.MaxValue;
        return (long) decimal.Round(
            (normalized - 0.5m) * spreadTicks,
            0,
            MidpointRounding.ToZero);
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

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _epoch = args.Epoch;
        _manualCivilAdvanceTicks = 0;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
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
        out NCCityCivilTimeSnapshot snapshot,
        out string? error)
    {
        if (!TryGetDefaultCivilRoutine(out var routine, out error))
        {
            snapshot = default;
            return false;
        }

        try
        {
            var elapsedCivilTicks = ScaleTicksFloor(
                now.Ticks,
                routine.CycleDuration.Ticks,
                routine.ActiveCycleDuration.Ticks);
            var totalCivilTicks = checked(
                elapsedCivilTicks + routine.CyclePhaseOffset.Ticks + _manualCivilAdvanceTicks);
            snapshot = new NCCityCivilTimeSnapshot(
                epoch,
                totalCivilTicks / routine.CycleDuration.Ticks + 1,
                TimeSpan.FromTicks(totalCivilTicks % routine.CycleDuration.Ticks),
                routine.CycleDuration,
                routine.ActiveCycleDuration,
                TimeSpan.FromTicks(_manualCivilAdvanceTicks));
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

    private bool TryGetDefaultCivilRoutine(
        out NCCityRoutineProfilePrototype routine,
        out string? error)
    {
        if (!_prototypes.TryIndex(DefaultCivilRoutine, out routine!) || !ValidateProfile(routine, out _))
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
    }
}
