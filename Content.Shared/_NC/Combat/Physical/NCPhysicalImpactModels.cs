// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Physical;

public enum NCPhysicalImpactSourceKind : byte
{
    Fall,
    FallingBody,
    ThrownObject,
    Collision,
    Scripted,
}

/// <summary>
/// Local trusted request raised by an engine source before it mutates compatibility HP.
/// </summary>
[ByRefEvent]
public record struct NCPhysicalImpactRequestedEvent(
    NCPhysicalImpactSourceKind SourceKind,
    DamageSpecifier Damage,
    EntityUid? Instigator,
    EntityUid? SourceEntity,
    Vector2 ImpactDirection,
    uint SourceSequence = 0,
    uint ImpactSequence = 0,
    NCActionResolutionId? ResolutionId = null,
    ProtoId<NCTargetZonePrototype>? ForcedZone = null,
    Vector2 LocalHitPosition = default,
    bool HasPhysicalHitPosition = false,
    bool Handled = false,
    DamageSpecifier? FeedbackDamage = null);

[Prototype("ncPhysicalImpactProfile")]
public sealed partial class NCPhysicalImpactProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField]
    public Dictionary<NCPhysicalImpactSourceKind, NCPhysicalImpactDefinition> Sources { get; private set; } = new();
}

[DataDefinition]
public sealed partial class NCPhysicalImpactDefinition
{
    /// <summary>
    /// Exact override for sources whose semantics are known independently of their vanilla damage key.
    /// Thrown weapons leave this null so slash/pierce/blunt is resolved from the real payload.
    /// </summary>
    [DataField]
    public ProtoId<NCCombatDamageProfilePrototype>? DamageProfile { get; private set; }

    [DataField]
    public float Contamination { get; private set; }
}
