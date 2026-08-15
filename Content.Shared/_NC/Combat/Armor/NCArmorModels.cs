// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Coarse impact classes used only to select applicable armor sections.
/// They are deterministic damage categories, not RED Core skills or checks.
/// </summary>
public enum NCArmorDamageClass : byte
{
    Ballistic,
    Blunt,
    Slash,
    Pierce,
    Blast,
    Thermal,
    Chemical,
    Electrical,
}

/// <summary>
/// Data-driven visual and examine condition of one armor section.
/// </summary>
public enum NCArmorConditionStage : byte
{
    Intact,
    Worn,
    Damaged,
    Critical,
    Destroyed,
}

/// <summary>
/// Stable address of one section on one armor entity.
/// </summary>
public readonly record struct NCArmorSectionHandle(
    EntityUid Source,
    ProtoId<NCArmorSectionPrototype> Section);

/// <summary>
/// Immutable section data collected for one physical impact.
/// </summary>
public readonly record struct NCArmorSectionSnapshot(
    NCArmorSectionHandle Handle,
    FixedPoint2 MaxSp,
    FixedPoint2 CurrentSp,
    bool Rigid,
    bool Sealed);

/// <summary>
/// Complete deterministic input for an armor resolution.
/// AblationAmount is supplied by the damage profile: standard 1, armor-piercing 2, rubber 0.
/// </summary>
public readonly record struct NCArmorResolveRequest(
    EntityUid Target,
    ProtoId<NCTargetZonePrototype> ActualZone,
    NCArmorDamageClass DamageClass,
    FixedPoint2 RawDamage,
    int AblationAmount);

/// <summary>
/// Before/after projection for a participating section. Mutation is performed separately by the server system.
/// </summary>
public readonly record struct NCArmorSectionResolution(
    NCArmorSectionHandle Handle,
    FixedPoint2 MaxSp,
    FixedPoint2 CurrentSpBefore,
    FixedPoint2 CurrentSpAfter,
    bool Rigid,
    bool Sealed,
    bool ProvidedEffectiveSp);

/// <summary>
/// Armor result for one actual hit zone. It never applies global HP damage.
/// </summary>
public sealed record NCArmorResolution(
    EntityUid Target,
    ProtoId<NCTargetZonePrototype> ActualZone,
    NCArmorDamageClass DamageClass,
    FixedPoint2 RawDamage,
    FixedPoint2 EffectiveSp,
    FixedPoint2 PostArmorDamage,
    bool Stopped,
    bool Penetrated,
    int AblationApplied,
    NCArmorSectionHandle? EffectiveSection,
    NCArmorSectionResolution[] ParticipatingSections)
{
    public bool HasArmor => ParticipatingSections.Length > 0;
}

/// <summary>
/// Raised only after authoritative section state actually changes.
/// </summary>
public readonly record struct NCArmorAblatedEvent(
    ProtoId<NCArmorSectionPrototype> Section,
    FixedPoint2 PreviousSp,
    FixedPoint2 CurrentSp,
    NCArmorConditionStage PreviousCondition,
    NCArmorConditionStage CurrentCondition);
