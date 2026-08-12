// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Combat.Explosion;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Explosion;

/// <summary>
/// Converts one resistance-adjusted vanilla explosion contact into bounded regional impacts. Vanilla explosion
/// propagation remains authoritative for distance, cover, contained-entity relays, ignition and throwing.
/// </summary>
public sealed partial class NCExplosionMedicalAdapterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly Dictionary<string, ProtoId<NCExplosionMedicalProfilePrototype>> _profilesByExplosion =
        new(StringComparer.Ordinal);
    private ProtoId<NCExplosionMedicalProfilePrototype>? _defaultProfile;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheProfiles();
    }

    /// <summary>
    /// Returns false only for non-NC entities so upstream Damageable behavior remains unchanged outside this system.
    /// An authoritative NC body always consumes the impact, including deliberately unsupported payload channels.
    /// </summary>
    public bool TryHandleExplosionDamage(
        EntityUid target,
        MapCoordinates epicenter,
        DamageSpecifier resistanceAdjustedDamage,
        string explosionPrototype,
        EntityUid? cause,
        NCActionResolutionId? trustedResolutionId = null)
    {
        if (!HasComp<NCMedicalBodyComponent>(target))
            return false;

        var resolutionId = trustedResolutionId is { IsValid: true } trusted
            ? trusted
            : NCActionResolutionId.New();
        if (!_routing.TryPartitionDamage(resistanceAdjustedDamage, out var partition))
        {
            Log.Error($"NC explosion '{explosionPrototype}' reached {ToPrettyString(target)} without a damage routing map; legacy HP was suppressed.");
            return true;
        }

        // One resistance-adjusted explosion payload may fan out into several local regions, but its whole-body
        // channels are applied once for this target/action before any regional trauma is delivered.
        var systemic = _routing.ApplySystemicSubset(
            target,
            resolutionId,
            NCSystemicDamageSourceKind.Explosion,
            0,
            resistanceAdjustedDamage);
        if (!systemic.Accepted)
            Log.Error($"NC explosion systemic payload was rejected on {ToPrettyString(target)}; compatibility HP was suppressed.");

        foreach (var healing in partition.RejectedHealing)
        {
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target, ref rejected);
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC explosion {unmapped.DamageType.Id} change on {ToPrettyString(target)} has no authoritative mapping; legacy HP was suppressed.");

        if (!partition.LocalTrauma.AnyPositive())
            return true;

        if (!TryResolveProfile(explosionPrototype, out var profile) ||
            profile.MaxAblationApplicationsPerSection <= 0 ||
            !NCExplosionMedicalRules.TryBuildDamage(
                profile.PrimaryImpact,
                partition.LocalTrauma,
                out var primaryDamage))
        {
            Log.Error($"NC explosion '{explosionPrototype}' reached {ToPrettyString(target)} without a valid medical profile; legacy HP was suppressed.");
            return true;
        }

        var direction = ResolveDirection(target, epicenter);
        var impactSequence = 0U;
        var primaryImpact = CreateImpact(
            resolutionId,
            impactSequence,
            target,
            cause,
            profile.PrimaryImpact.DamageProfile,
            primaryDamage,
            direction);
        if (!_delivery.TryDeliver(primaryImpact, out var primaryResult))
        {
            Log.Error($"NC explosion '{explosionPrototype}' could not deliver its primary impact to {ToPrettyString(target)}; legacy HP was suppressed.");
            return true;
        }

        var ablationCounts = new Dictionary<NCArmorSectionHandle, int>();
        RecordAblation(primaryResult, null, ablationCounts);

        var neighborZones = BuildNeighborOrder(primaryResult.ActualZone);
        var neighborCursor = neighborZones.Count == 0
            ? 0
            : (int) (resolutionId.DeriveSeed(1) % (uint) neighborZones.Count);

        foreach (var definition in profile.SecondaryImpacts)
        {
            if (!NCExplosionMedicalRules.TryBuildDamage(
                    definition,
                    partition.LocalTrauma,
                    out var channelDamage))
            {
                continue;
            }

            var count = Math.Max(0, definition.Count);
            for (var i = 0; i < count; i++)
            {
                ProtoId<NCTargetZonePrototype> zone;
                if (definition.ZoneSelection == NCExplosionZoneSelection.Primary || neighborZones.Count == 0)
                {
                    zone = primaryResult.ActualZone;
                }
                else
                {
                    zone = neighborZones[neighborCursor % neighborZones.Count];
                    neighborCursor++;
                }

                impactSequence++;
                var exclusions = BuildAblationExclusions(
                    ablationCounts,
                    profile.MaxAblationApplicationsPerSection);
                var secondaryImpact = CreateImpact(
                        resolutionId,
                        impactSequence,
                        target,
                        cause,
                        definition.DamageProfile,
                        channelDamage,
                        direction) with
                    {
                        ForcedZone = zone,
                        AblationExclusions = exclusions,
                    };

                if (_delivery.TryDeliver(secondaryImpact, out var secondaryResult))
                    RecordAblation(secondaryResult, exclusions, ablationCounts);
            }
        }

        return true;
    }

    private NCResolvedCombatImpact CreateImpact(
        NCActionResolutionId resolutionId,
        uint impactSequence,
        EntityUid target,
        EntityUid? cause,
        ProtoId<Content.Shared._NC.Combat.Damage.NCCombatDamageProfilePrototype> damageProfile,
        DamageSpecifier damage,
        Vector2 direction)
    {
        return new NCResolvedCombatImpact(
            resolutionId,
            0,
            impactSequence,
            resolutionId.DeriveSeed(impactSequence),
            target,
            cause,
            cause,
            null,
            null,
            NCTargetingMode.Automatic,
            null,
            damageProfile,
            damage,
            Vector2.Zero,
            direction,
            0f,
            _timing.CurTime);
    }

    private List<ProtoId<NCTargetZonePrototype>> BuildNeighborOrder(
        ProtoId<NCTargetZonePrototype> primary)
    {
        var result = new List<ProtoId<NCTargetZonePrototype>>();
        var visited = new HashSet<ProtoId<NCTargetZonePrototype>> { primary };
        var pending = new Queue<ProtoId<NCTargetZonePrototype>>();
        pending.Enqueue(primary);

        // Breadth-first traversal preserves the ordered prototype neighbor graph and remains bounded by target zones.
        while (pending.TryDequeue(out var current))
        {
            if (!_prototypes.TryIndex(current, out NCTargetZonePrototype? zone))
                continue;

            foreach (var neighbor in zone.NeighborZones)
            {
                if (!visited.Add(neighbor) || !_prototypes.HasIndex(neighbor))
                    continue;

                result.Add(neighbor);
                pending.Enqueue(neighbor);
            }
        }

        return result;
    }

    private Vector2 ResolveDirection(EntityUid target, MapCoordinates epicenter)
    {
        var targetPosition = _transform.GetMapCoordinates(target);
        if (targetPosition.MapId != epicenter.MapId)
            return Vector2.Zero;

        return targetPosition.Position - epicenter.Position;
    }

    private static IReadOnlySet<NCArmorSectionHandle>? BuildAblationExclusions(
        IReadOnlyDictionary<NCArmorSectionHandle, int> counts,
        int maximumApplications)
    {
        if (counts.Count == 0 || maximumApplications <= 0)
            return maximumApplications <= 0 ? new HashSet<NCArmorSectionHandle>() : null;

        var excluded = new HashSet<NCArmorSectionHandle>();
        foreach (var (section, count) in counts)
        {
            if (count >= maximumApplications)
                excluded.Add(section);
        }

        return excluded.Count == 0 ? null : excluded;
    }

    private static void RecordAblation(
        NCCombatTraumaDeliveryResult result,
        IReadOnlySet<NCArmorSectionHandle>? exclusions,
        Dictionary<NCArmorSectionHandle, int> counts)
    {
        if (!result.Applied || !result.Armor.Penetrated || result.Armor.AblationApplied <= 0)
            return;

        foreach (var section in result.Armor.ParticipatingSections)
        {
            if (exclusions?.Contains(section.Handle) == true)
                continue;

            counts[section.Handle] = counts.GetValueOrDefault(section.Handle) + 1;
        }
    }

    private bool TryResolveProfile(
        string explosionPrototype,
        out NCExplosionMedicalProfilePrototype profile)
    {
        if (_profilesByExplosion.TryGetValue(explosionPrototype, out var explicitProfile) &&
            _prototypes.TryIndex(explicitProfile, out NCExplosionMedicalProfilePrototype? explicitResolved) &&
            explicitResolved is { } explicitValue)
        {
            profile = explicitValue;
            return true;
        }

        if (_defaultProfile is { } fallback &&
            _prototypes.TryIndex(fallback, out NCExplosionMedicalProfilePrototype? fallbackResolved) &&
            fallbackResolved is { } fallbackValue)
        {
            profile = fallbackValue;
            return true;
        }

        profile = default!;
        return false;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheProfiles();
    }

    private void CacheProfiles()
    {
        _profilesByExplosion.Clear();
        _defaultProfile = null;

        var profiles = _prototypes.EnumeratePrototypes<NCExplosionMedicalProfilePrototype>()
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();
        foreach (var profile in profiles)
        {
            if (profile.DefaultForHumans)
            {
                if (_defaultProfile != null)
                    Log.Error("More than one default human ncExplosionMedicalProfile is defined.");
                else
                    _defaultProfile = profile.ID;
            }

            foreach (var explosion in profile.ExplosionPrototypes)
            {
                if (!_profilesByExplosion.TryAdd(explosion.Id, profile.ID))
                    Log.Error($"Explosion prototype '{explosion.Id}' is assigned to more than one NC medical profile.");
            }
        }
    }
}
