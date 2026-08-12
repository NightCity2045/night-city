// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server._NC.Combat.Armor;
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
}

/// <summary>
/// Small adapter result. Detailed wounds and physiology remain owned by medicine.
/// </summary>
public sealed record NCCombatTraumaDeliveryResult(
    ProtoId<NCTargetZonePrototype> ActualZone,
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
    [Dependency] private NCAnatomySystem _anatomy = default!;
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

        ProtoId<NCTargetZonePrototype> actualZone;
        bool resolved;
        if (impact.ForcedZone is { } forcedZone)
        {
            // Trusted adapters still pass through live anatomy validation, including missing-part fallback.
            resolved = _anatomy.TryResolveSelectedZone(
                (impact.Target, medicalBody),
                forcedZone,
                out actualZone,
                out _);
        }
        else
        {
            var selectedApplies = impact.IntendedTarget == impact.Target &&
                                  impact.TargetingMode == NCTargetingMode.SelectedZone &&
                                  impact.SelectedZone is { } selectedZone;
            resolved = selectedApplies
                ? _anatomy.TryResolveSelectedZone(
                    (impact.Target, medicalBody),
                    selectedZone,
                    out actualZone,
                    out _)
                : _anatomy.TryResolveAutomaticZone(
                    (impact.Target, medicalBody),
                    impact.Seed,
                    out actualZone,
                    out _);
        }

        if (!resolved ||
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
            impact.Timestamp);

        var traumaEvent = new NCCombatTraumaRequestedEvent(request);
        RaiseLocalEvent(impact.Target, ref traumaEvent);
        if (!traumaEvent.Handled && !traumaEvent.Cancelled)
            return false;

        if (traumaEvent.Handled && traumaEvent.Applied)
            _armor.ApplyAblation(armorResolution, impact.AblationExclusions);

        var appliedTrauma = FixedPoint2.Max(armorResolution.PostArmorDamage, transferredBluntDamage);
        result = new NCCombatTraumaDeliveryResult(
            actualZone,
            zone.MedicalRegionCategory,
            armorResolution,
            appliedTrauma,
            ScaleDamage(impact.Damage, rawDamage, appliedTrauma),
            traumaEvent.Applied,
            traumaEvent.Cancelled,
            damageProfile.CanOverpenetrateBody);

        var source = impact.Projectile ?? impact.Weapon ?? impact.Instigator;
        var sourceDescription = source is { } sourceUid && Exists(sourceUid)
            ? ToPrettyString(sourceUid)
            : "environment";
        _adminLogger.Add(
            LogType.Damaged,
            LogImpact.Medium,
            $"NC impact {impact.ResolutionId:resolution} from {sourceDescription:source} hit " +
            $"{ToPrettyString(impact.Target):target} in {actualZone.Id:zone}; raw {rawDamage:damage}, " +
            $"effective SP {armorResolution.EffectiveSp:sp}, local trauma {appliedTrauma:damage}, " +
            $"accepted {traumaEvent.Handled:handled}, applied {traumaEvent.Applied:applied}, " +
            $"cancelled {traumaEvent.Cancelled:cancelled}.");
        return true;
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
