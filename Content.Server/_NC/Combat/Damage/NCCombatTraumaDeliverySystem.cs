// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server._NC.Combat.Armor;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Damage;

/// <summary>
/// Source-neutral immutable input produced only after a projectile, melee, hitscan or environment adapter
/// has established a real physical impact. Seed selects an anatomical zone; it never decides hit or miss.
/// </summary>
public readonly record struct NCResolvedCombatImpact(
    NCActionResolutionId ResolutionId,
    uint SourceSequence,
    uint ImpactSequence,
    ulong Seed,
    EntityUid Target,
    EntityUid? Instigator,
    EntityUid? Weapon,
    EntityUid? Projectile,
    EntityUid? IntendedTarget,
    NCTargetingMode TargetingMode,
    ProtoId<NCTargetZonePrototype>? SelectedZone,
    ProtoId<NCCombatDamageProfilePrototype> DamageProfile,
    DamageSpecifier Damage,
    Vector2 TargetLocalHitPosition,
    Vector2 ImpactDirection,
    float Contamination,
    TimeSpan Timestamp)
{
    /// <summary>
    /// Server-only anatomical override produced by trusted physical adapters such as distributed explosions.
    /// Player input never writes this field; ordinary attacks still resolve from their snapshotted targeting intent.
    /// </summary>
    public ProtoId<NCTargetZonePrototype>? ForcedZone { get; init; }

    /// <summary>
    /// Armor sections that already consumed their ablation budget for this compound physical event.
    /// This is used by distributed explosions so aggregated regional impacts cannot shred one item repeatedly.
    /// </summary>
    public IReadOnlySet<NCArmorSectionHandle>? AblationExclusions { get; init; }

    /// <summary>
    /// True only when the adapter received an actual contact point from physics/raycast geometry.
    /// Synthetic source positions must not masquerade as anatomical collision evidence.
    /// </summary>
    public bool HasPhysicalHitPosition { get; init; }
}

/// <summary>
/// Small adapter result. Detailed wounds and physiology remain owned by medicine.
/// </summary>
public sealed record NCCombatTraumaDeliveryResult(
    ProtoId<NCTargetZonePrototype> ActualZone,
    NCHitLocationSource LocationSource,
    ProtoId<Content.Shared.Body.OrganCategoryPrototype> MedicalRegion,
    NCArmorResolution Armor,
    FixedPoint2 AppliedTrauma,
    DamageSpecifier FeedbackDamage,
    bool Applied,
    bool Cancelled,
    bool CanOverpenetrateBody);

/// <summary>
/// The single armor-to-medical delivery path for every NC physical damage adapter.
/// </summary>
public sealed partial class NCCombatTraumaDeliverySystem : EntitySystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCHitLocationSystem _hitLocations = default!;
    [Dependency] private NCArmorResolutionSystem _armor = default!;

    /// <summary>
    /// Returns true only when authoritative medicine accepted or explicitly cancelled the impact.
    /// Armor mutation occurs after acceptance, so a missing handler cannot ablate armor before legacy fallback.
    /// </summary>
    public bool TryDeliver(
        in NCResolvedCombatImpact impact,
        out NCCombatTraumaDeliveryResult result)
    {
        result = default!;
        if (!TryComp<NCMedicalBodyComponent>(impact.Target, out var medicalBody) ||
            !_prototypes.TryIndex(impact.DamageProfile, out NCCombatDamageProfilePrototype? damageProfile))
        {
            return false;
        }

        var rawDamage = FixedPoint2.Max(FixedPoint2.Zero, impact.Damage.GetTotal());
        if (rawDamage <= FixedPoint2.Zero)
            return false;

        if (!_hitLocations.TryResolve(
                impact,
                (impact.Target, medicalBody),
                out var actualZone,
                out _,
                out var locationSource) ||
            !_prototypes.TryIndex(actualZone, out NCTargetZonePrototype? zone))
        {
            return false;
        }

        var armorRequest = new NCArmorResolveRequest(
            impact.Target,
            actualZone,
            damageProfile.ArmorDamageClass,
            rawDamage,
            damageProfile.AblationAmount);
        var armorResolution = _armor.ResolveBound(armorRequest);

        var transferredBluntDamage = FixedPoint2.Zero;
        var traumaProfile = damageProfile.TraumaProfile;
        if (armorResolution.Stopped &&
            damageProfile.BluntTransferProfile is { } transferProfileId &&
            _prototypes.TryIndex(transferProfileId, out NCBluntTransferProfilePrototype? transferProfile))
        {
            transferredBluntDamage = NCBluntTransferRules.Resolve(transferProfile, armorResolution);
            if (transferredBluntDamage > FixedPoint2.Zero)
                traumaProfile = transferProfile.TraumaProfile;
        }

        var flags = damageProfile.TraumaFlags;
        if (armorResolution.Stopped)
            flags |= NCTraumaFlags.ArmorStopped;
        if (armorResolution.Penetrated)
            flags |= NCTraumaFlags.ArmorPenetrated;

        var projectileDisposition = ResolveProjectileDisposition(damageProfile, armorResolution.PostArmorDamage);

        // DamageSpecifier is mutable, so medicine receives a defensive copy.
        var request = new NCCombatTraumaRequest(
            impact.ResolutionId,
            impact.SourceSequence,
            impact.ImpactSequence,
            impact.Target,
            impact.Instigator,
            impact.Weapon,
            impact.Projectile,
            actualZone,
            zone.MedicalRegionCategory,
            traumaProfile,
            new DamageSpecifier(impact.Damage),
            rawDamage,
            armorResolution.EffectiveSp,
            armorResolution.PostArmorDamage,
            transferredBluntDamage,
            impact.TargetLocalHitPosition,
            impact.ImpactDirection.IsLengthZero() ? Vector2.Zero : impact.ImpactDirection.Normalized(),
            flags,
            Math.Clamp(impact.Contamination, 0f, 1f),
            impact.Timestamp)
        {
            ProjectileDisposition = projectileDisposition,
            ExitTraumaMultiplier = damageProfile.ExitTraumaMultiplier,
        };

        var traumaEvent = new NCCombatTraumaRequestedEvent(request);
        RaiseLocalEvent(impact.Target, ref traumaEvent);
        if (!traumaEvent.Handled && !traumaEvent.Cancelled)
            return false;

        if (traumaEvent.Handled && traumaEvent.Applied)
            _armor.ApplyAblation(armorResolution, impact.AblationExclusions);

        var appliedTrauma = FixedPoint2.Max(armorResolution.PostArmorDamage, transferredBluntDamage);
        result = new NCCombatTraumaDeliveryResult(
            actualZone,
            locationSource,
            zone.MedicalRegionCategory,
            armorResolution,
            appliedTrauma,
            ScaleDamage(impact.Damage, rawDamage, appliedTrauma),
            traumaEvent.Applied,
            traumaEvent.Cancelled,
            damageProfile.CanOverpenetrateBody && projectileDisposition == NCTraumaProjectileDisposition.Exit);

        var source = impact.Projectile ?? impact.Weapon ?? impact.Instigator;
        var sourceDescription = source is { } sourceUid && Exists(sourceUid)
            ? ToPrettyString(sourceUid)
            : "environment";
        _adminLogger.Add(
            LogType.Damaged,
            LogImpact.Medium,
            // LogStringHandler forwards format labels to the runtime formatter for generic values.
            // Enums, booleans and NC value types therefore must not use structured entity labels.
            $"NC impact {impact.ResolutionId} from {sourceDescription} hit " +
            $"{ToPrettyString(impact.Target):target} in {actualZone.Id} via {locationSource}; raw {rawDamage}, " +
            $"effective SP {armorResolution.EffectiveSp}, local trauma {appliedTrauma}, " +
            $"accepted {traumaEvent.Handled}, applied {traumaEvent.Applied}, " +
            $"cancelled {traumaEvent.Cancelled}.");
        return true;
    }

    private static NCTraumaProjectileDisposition ResolveProjectileDisposition(
        NCCombatDamageProfilePrototype profile,
        FixedPoint2 postArmorDamage)
    {
        var damage = postArmorDamage.Float();
        if (damage <= 0f)
            return NCTraumaProjectileDisposition.None;

        if ((profile.TraumaFlags & NCTraumaFlags.CanExit) != 0 &&
            profile.ExitMinimumPostArmorDamage > 0f &&
            damage >= profile.ExitMinimumPostArmorDamage)
        {
            return NCTraumaProjectileDisposition.Exit;
        }

        if ((profile.TraumaFlags & NCTraumaFlags.CanEmbed) != 0 &&
            profile.EmbedMinimumPostArmorDamage > 0f &&
            damage >= profile.EmbedMinimumPostArmorDamage)
        {
            return NCTraumaProjectileDisposition.Embedded;
        }

        return NCTraumaProjectileDisposition.None;
    }

    private static DamageSpecifier ScaleDamage(
        DamageSpecifier source,
        FixedPoint2 rawDamage,
        FixedPoint2 appliedDamage)
    {
        if (rawDamage <= FixedPoint2.Zero || appliedDamage <= FixedPoint2.Zero)
            return new DamageSpecifier();

        return source * (appliedDamage / rawDamage);
    }
}
