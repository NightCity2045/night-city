// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
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

/// <summary>
/// Deterministically evaluates cyclic routines and bounded actor commitments against active-world time.
/// It does not move actors or perform persistence; the schedule scheduler owns those side effects.
/// </summary>
public sealed partial class NCCityRoutineSystem : EntitySystem
{
    private const int MaximumRoutineEntries = 64;
    private const int MaximumTextLength = 128;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;

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
        if (profile.CycleDuration <= TimeSpan.Zero)
        {
            error = "Cycle duration must be positive.";
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
                if (!entry.ExplicitZone.IsValid || !_zones.TryGetZone(entry.ExplicitZone, out _))
                {
                    error = $"Entry at {entry.At} references a missing explicit zone.";
                    return false;
                }
            }
            else if (entry.ExplicitZone.IsValid)
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
        var positionTicks = now.Ticks % cycleTicks;
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
            NCCityRoutineTarget.ExplicitZone => entry.ExplicitZone,
            _ => actor.Location.CurrentZone,
        };
        var travelMode = entry.TravelMode == NCCityTravelMode.None
            ? defaultTravelMode
            : entry.TravelMode;
        var presenceMode = entry.Target == NCCityRoutineTarget.Home
            ? ResolveHomePresence(actor.Location.HomeZone)
            : NCCityActorPresenceMode.Public;

        return new NCCityRoutineDecision(
            NCCityRoutineDecisionSource.Routine,
            default,
            entry.Activity,
            entry.Goal,
            targetZone,
            travelMode,
            entry.Availability,
            presenceMode,
            now.Advance(TimeSpan.FromTicks(ticksUntilNext)));
    }

    private NCCityActorPresenceMode ResolveHomePresence(NCZoneId homeZone)
    {
        return _zones.TryGetZone(homeZone, out var zone) &&
               zone.ContextSlot == NCZoneContextSlot.Apartment
            ? NCCityActorPresenceMode.PhysicalHome
            : NCCityActorPresenceMode.AbstractHome;
    }

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }
}
