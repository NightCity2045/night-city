// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Combat.Physical;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Physical;

/// <summary>
/// Converts validated falls, thrown contacts and kinetic collisions into the single NC delivery path.
/// Source systems retain physics, sounds and recoil; this system exclusively replaces human HP mutation.
/// </summary>
public sealed partial class NCPhysicalImpactAdapterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;

    private ProtoId<NCPhysicalImpactProfilePrototype>? _defaultProfile;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMedicalBodyComponent, NCPhysicalImpactRequestedEvent>(OnPhysicalImpact);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfile();
    }

    public bool TryApply(EntityUid target, ref NCPhysicalImpactRequestedEvent request)
    {
        if (!HasComp<NCMedicalBodyComponent>(target))
            return false;

        RaiseLocalEvent(target, ref request);
        return request.Handled;
    }

    private void OnPhysicalImpact(
        Entity<NCMedicalBodyComponent> target,
        ref NCPhysicalImpactRequestedEvent request)
    {
        request.Handled = true;
        request.FeedbackDamage = new DamageSpecifier();

        if (!_routing.TryPartitionDamage(request.Damage, out var partition) ||
            !TryGetDefinition(request.SourceKind, out var definition))
        {
            Log.Error($"NC physical impact {request.SourceKind} on {ToPrettyString(target.Owner)} has no valid data profile; compatibility HP was suppressed.");
            return;
        }

        var resolution = request.ResolutionId is { IsValid: true } trusted
            ? trusted
            : NCActionResolutionId.New();
        var systemic = _routing.ApplySystemicSubset(
            target.Owner,
            resolution,
            ResolveSystemicSource(request.SourceKind),
            request.SourceSequence,
            request.ImpactSequence,
            request.Damage);
        if (!systemic.Accepted)
            Log.Error($"NC physical systemic payload {request.SourceKind} was rejected on {ToPrettyString(target.Owner)}.");

        foreach (var healing in partition.RejectedHealing)
        {
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target.Owner, ref rejected);
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC physical impact {request.SourceKind} has no mapping for {unmapped.DamageType.Id}; compatibility HP was suppressed.");

        if (!partition.LocalTrauma.AnyPositive())
            return;

        var damageProfile = definition.DamageProfile;
        if (damageProfile is not { } resolvedProfile &&
            !_routing.TryResolveTraumaProfile(partition.LocalTrauma, out resolvedProfile))
        {
            Log.Error($"NC physical impact {request.SourceKind} could not resolve local trauma on {ToPrettyString(target.Owner)}.");
            return;
        }

        var impact = new NCResolvedCombatImpact(
            resolution,
            request.SourceSequence,
            request.ImpactSequence,
            resolution.DeriveSeed(request.SourceSequence ^ request.ImpactSequence),
            target.Owner,
            request.Instigator,
            request.SourceEntity,
            null,
            null,
            NCTargetingMode.Automatic,
            null,
            resolvedProfile,
            partition.LocalTrauma,
            request.LocalHitPosition,
            request.ImpactDirection,
            definition.Contamination,
            _timing.CurTime)
        {
            ForcedZone = request.ForcedZone,
            HasPhysicalHitPosition = request.HasPhysicalHitPosition,
        };

        if (_delivery.TryDeliver(impact, out var delivered))
            request.FeedbackDamage = delivered.FeedbackDamage;
        else
            Log.Error($"NC physical impact {request.SourceKind} delivery failed on {ToPrettyString(target.Owner)}.");
    }

    private bool TryGetDefinition(
        NCPhysicalImpactSourceKind source,
        out NCPhysicalImpactDefinition definition)
    {
        definition = default!;
        if (_defaultProfile is not { } profileId ||
            !_prototypes.TryIndex(profileId, out NCPhysicalImpactProfilePrototype? profile))
        {
            CacheDefaultProfile();
            if (_defaultProfile is not { } refreshed ||
                !_prototypes.TryIndex(refreshed, out profile))
            {
                return false;
            }
        }

        return profile.Sources.TryGetValue(source, out definition!);
    }

    private static NCSystemicDamageSourceKind ResolveSystemicSource(NCPhysicalImpactSourceKind source)
    {
        return source switch
        {
            NCPhysicalImpactSourceKind.Fall or NCPhysicalImpactSourceKind.FallingBody =>
                NCSystemicDamageSourceKind.Fall,
            NCPhysicalImpactSourceKind.ThrownObject => NCSystemicDamageSourceKind.ThrownObject,
            NCPhysicalImpactSourceKind.Collision => NCSystemicDamageSourceKind.Collision,
            _ => NCSystemicDamageSourceKind.Scripted,
        };
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheDefaultProfile();
    }

    private void CacheDefaultProfile()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCPhysicalImpactProfilePrototype>()
            .Where(profile => profile.DefaultForHumans)
            .OrderBy(profile => profile.ID, StringComparer.Ordinal)
            .ToArray();
        _defaultProfile = defaults.Length == 1 ? defaults[0].ID : null;
        if (defaults.Length > 1)
            Log.Error("More than one default human ncPhysicalImpactProfile is defined.");
    }
}
