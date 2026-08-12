// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Trauma;

/// <summary>
/// Immutable boundary between a resolved physical impact and authoritative medicine.
/// It contains discrete combat values rather than an energy simulation or a second hit check.
/// </summary>
public readonly record struct NCCombatTraumaRequest(
    NCActionResolutionId ResolutionId,
    uint ProjectileSequence,
    uint ImpactSequence,
    EntityUid Target,
    EntityUid? Instigator,
    EntityUid? Weapon,
    EntityUid? Projectile,
    ProtoId<NCTargetZonePrototype> ActualZone,
    ProtoId<OrganCategoryPrototype> MedicalRegion,
    ProtoId<NCTraumaProfilePrototype> TraumaProfile,
    DamageSpecifier Damage,
    FixedPoint2 RawDamage,
    FixedPoint2 EffectiveSp,
    FixedPoint2 PostArmorDamage,
    FixedPoint2 TransferredBluntDamage,
    Vector2 TargetLocalHitPosition,
    Vector2 ImpactDirection,
    NCTraumaFlags Flags,
    float Contamination,
    TimeSpan Timestamp);

/// <summary>
/// Cancellable local event used by projectile, melee and environment adapters.
/// Handled means authoritative medicine accepted ownership and vanilla body damage must be skipped.
/// </summary>
[ByRefEvent]
public record struct NCCombatTraumaRequestedEvent(
    NCCombatTraumaRequest Request,
    bool Handled = false,
    bool Applied = false,
    bool Cancelled = false);

/// <summary>
/// Small result shared with audit and feedback systems. Detailed wound records remain server-only.
/// </summary>
public readonly record struct NCCombatTraumaResult(
    NCActionResolutionId ResolutionId,
    ProtoId<NCTargetZonePrototype> ActualZone,
    ProtoId<OrganCategoryPrototype> MedicalRegion,
    FixedPoint2 AppliedTrauma,
    int WoundsCreated,
    bool ExternalBleedingChanged,
    bool InternalBleedingChanged,
    bool FunctionChanged,
    bool EmbeddedObject,
    bool PassThrough);

[Flags]
public enum NCTraumaFlags : ushort
{
    None = 0,
    Penetrating = 1 << 0,
    ArmorStopped = 1 << 1,
    ArmorPenetrated = 1 << 2,
    NonLethal = 1 << 3,
    ArmorPiercing = 1 << 4,
    Incendiary = 1 << 5,
    CanEmbed = 1 << 6,
    CanExit = 1 << 7,
    Critical = 1 << 8,
}
