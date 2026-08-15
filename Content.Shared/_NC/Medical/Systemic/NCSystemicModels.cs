// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Systemic;

/// <summary>
/// Sanitized aggregate consumed by physiology. Callers never receive the mutable burden dictionary.
/// </summary>
public readonly record struct NCSystemicPhysiologyEffects(
    float OxygenationMultiplier,
    float CardiacOutputMultiplier,
    float ShockContribution,
    float BrainFunctionMultiplier,
    float RhythmInstability,
    bool UnstableRhythm,
    bool ShockableArrest);

public readonly record struct NCSystemicApplicationResult(
    bool Accepted,
    NCSystemicChannel Channel,
    float PreviousBurden,
    float CurrentBurden,
    bool ShockableArrest);

public enum NCSystemicDamageSourceKind : byte
{
    Environment,
    Projectile,
    Hitscan,
    Melee,
    Explosion,
    Radiation,
    Atmosphere,
    Reagent,
    Scripted,
    Fall,
    ThrownObject,
    Collision,
}

public readonly record struct NCProcessedSystemicPayloadKey(
    Guid ResolutionId,
    NCSystemicDamageSourceKind Source,
    uint Sequence,
    uint ImpactSequence);

public readonly record struct NCSystemicPayloadResult(
    bool Accepted,
    bool Duplicate,
    int AppliedChannels);

/// <summary>
/// Event-facing request for measured server world state that does not originate in Damageable. Existing atmos
/// and reagent paths use the compatibility adapter; radiation and future hazards may call this same contract.
/// </summary>
[ByRefEvent]
public record struct NCSystemicExposureEvent(
    NCActionResolutionId ResolutionId,
    NCSystemicDamageSourceKind Source,
    uint Sequence,
    ProtoId<DamageTypePrototype> DamageType,
    ProtoId<NCSystemicDamageProfilePrototype> Profile,
    float Amount)
{
    public bool Handled { get; set; }
}

[ByRefEvent]
public readonly record struct NCSystemicStateChangedEvent(
    NCSystemicChannel Channel,
    float PreviousBurden,
    float CurrentBurden,
    bool ShockableArrest);
