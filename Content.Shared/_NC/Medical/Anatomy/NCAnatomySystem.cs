// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Resolves NC zones against the materialized SS14 organ graph.
/// Every lookup is bounded to one body's small organ container and never scans the world.
/// </summary>
public sealed partial class NCAnatomySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    public bool TryGetProfile(
        Entity<NCMedicalBodyComponent?> body,
        [NotNullWhen(true)] out NCAnatomyProfilePrototype? profile)
    {
        profile = null;
        return Resolve(body, ref body.Comp, false) &&
               _prototypes.TryIndex(body.Comp.Profile, out profile);
    }

    public bool TryGetOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category, out EntityUid organ)
    {
        organ = default;
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return false;

        foreach (var candidate in bodyComponent.Organs.ContainedEntities)
        {
            if (!TryComp<OrganComponent>(candidate, out var organComponent) ||
                organComponent.Category != category)
            {
                continue;
            }

            organ = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a configured target zone exactly. Missing regions are not silently replaced here.
    /// </summary>
    public bool TryGetZone(
        Entity<NCMedicalBodyComponent?> body,
        ProtoId<NCTargetZonePrototype> zoneId,
        [NotNullWhen(true)] out NCTargetZonePrototype? zone,
        out EntityUid region)
    {
        zone = null;
        region = default;
        return TryGetProfile(body, out var profile) &&
               profile.TargetZones.Contains(zoneId) &&
               _prototypes.TryIndex(zoneId, out zone) &&
               TryGetOrgan(body, zone.MedicalRegionCategory, out region);
    }

    /// <summary>
    /// Resolves a selected zone and its deterministic fallback chain on the actual victim.
    /// </summary>
    public bool TryResolveSelectedZone(
        Entity<NCMedicalBodyComponent?> body,
        ProtoId<NCTargetZonePrototype> requested,
        out ProtoId<NCTargetZonePrototype> actual,
        out EntityUid region)
    {
        actual = default;
        region = default;
        if (!TryGetProfile(body, out var profile) || !profile.TargetZones.Contains(requested))
            return false;

        var visited = new HashSet<ProtoId<NCTargetZonePrototype>>();
        ProtoId<NCTargetZonePrototype>? current = requested;
        while (current is { } zoneId && visited.Add(zoneId))
        {
            if (!profile.TargetZones.Contains(zoneId) || !_prototypes.TryIndex(zoneId, out var zone))
                return false;

            if (TryGetOrgan(body, zone.MedicalRegionCategory, out region))
            {
                actual = zoneId;
                return true;
            }

            current = zone.FallbackZone;
        }

        return false;
    }

    /// <summary>
    /// Performs a reproducible weighted selection. Missing zones donate their weight to their configured fallback.
    /// The caller supplies a server seed derived from the immutable physical-action context.
    /// </summary>
    public bool TryResolveAutomaticZone(
        Entity<NCMedicalBodyComponent?> body,
        ulong seed,
        out ProtoId<NCTargetZonePrototype> actual,
        out EntityUid region)
    {
        actual = default;
        region = default;
        if (!TryGetProfile(body, out var profile))
            return false;

        // Preserve prototype order explicitly. Dictionary enumeration order is an implementation detail and
        // cannot be part of the replay/network determinism contract for an authoritative combat resolver.
        var candidateWeights = new Dictionary<ProtoId<NCTargetZonePrototype>, float>();
        var orderedCandidates = new List<ProtoId<NCTargetZonePrototype>>();
        var totalWeight = 0f;
        foreach (var configured in profile.TargetZones)
        {
            if (!_prototypes.TryIndex(configured, out var zone) ||
                zone.AutomaticWeight <= 0f ||
                !float.IsFinite(zone.AutomaticWeight) ||
                !TryResolveSelectedZone(body, configured, out var resolved, out _))
            {
                continue;
            }

            if (!candidateWeights.TryGetValue(resolved, out var existingWeight))
                orderedCandidates.Add(resolved);

            candidateWeights[resolved] = existingWeight + zone.AutomaticWeight;
            totalWeight += zone.AutomaticWeight;
        }

        if (orderedCandidates.Count == 0 || totalWeight <= 0f || !float.IsFinite(totalWeight))
            return TryResolveSelectedZone(body, profile.AutomaticFallbackZone, out actual, out region);

        var cursor = ToUnitInterval(SplitMix64(seed)) * totalWeight;
        foreach (var candidate in orderedCandidates)
        {
            var weight = candidateWeights[candidate];
            cursor -= weight;
            if (cursor > 0f)
                continue;

            return TryResolveSelectedZone(body, candidate, out actual, out region);
        }

        return TryResolveSelectedZone(body, profile.AutomaticFallbackZone, out actual, out region);
    }

    private static ulong SplitMix64(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ value >> 30) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ value >> 27) * 0x94D049BB133111EBUL;
        return value ^ value >> 31;
    }

    private static float ToUnitInterval(ulong value)
    {
        return (float) ((value >> 40) * (1.0 / (1UL << 24)));
    }
}
