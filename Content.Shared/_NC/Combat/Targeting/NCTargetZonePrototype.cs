// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Body;
using System.Numerics;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Stable gameplay identity shared by combat, treatment, diagnosis and surgery.
/// A target zone maps presentation intent to the live SS14 organ graph.
/// </summary>
[Prototype("ncTargetZone")]
public sealed partial class NCTargetZonePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId DisplayName { get; private set; } = default!;

    /// <summary>
    /// Upstream visible body part associated with the zone.
    /// Chest, abdomen and groin deliberately share the visible torso.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> ExternalOrganCategory { get; private set; }

    /// <summary>
    /// Materialized NC region that receives the local trauma.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> MedicalRegionCategory { get; private set; }

    [DataField]
    public float AutomaticWeight { get; private set; }

    [DataField]
    public bool Selectable { get; private set; } = true;

    [DataField]
    public bool StandardCombatAllowed { get; private set; } = true;

    [DataField]
    public NCArmorLocation ArmorLocation { get; private set; }

    /// <summary>
    /// Ordered deterministic fallback used when the materialized region is missing.
    /// Cycles are rejected by semantic prototype tests.
    /// </summary>
    [DataField]
    public ProtoId<NCTargetZonePrototype>? FallbackZone { get; private set; }

    [DataField]
    public List<ProtoId<NCTargetZonePrototype>> NeighborZones { get; private set; } = [];

    /// <summary>
    /// Optional normalized footprint rectangle used when physics supplied a real contact point. Coordinates
    /// are in the target's local [-0.5, 0.5] footprint converted to [0, 1]. Overlap is rejected by tests.
    /// </summary>
    [DataField]
    public NCTargetZoneHitBounds? HitBounds { get; private set; }

    /// <summary>
    /// Resolves intentionally nested precision zones. Higher-priority rectangles win physical overlap and
    /// their doll buttons are placed above broader neighbours, so eyes, mouth and groin remain clickable.
    /// </summary>
    [DataField]
    public int HitPriority { get; private set; }

    /// <summary>
    /// Multiplier applied to automatic selection while the target is down. This changes anatomical exposure,
    /// never whether the physical attack hit.
    /// </summary>
    [DataField]
    public float DownedWeightMultiplier { get; private set; } = 1f;

    /// <summary>
    /// Optional exposure multipliers for the local direction from which the impact arrived.
    /// </summary>
    [DataField]
    public Dictionary<NCHitApproachDirection, float> DirectionWeightMultipliers { get; private set; } = new();
}

[DataDefinition]
public sealed partial class NCTargetZoneHitBounds
{
    [DataField(required: true)]
    public Vector2 Position { get; private set; }

    [DataField(required: true)]
    public Vector2 Size { get; private set; }
}

public enum NCHitApproachDirection : byte
{
    Front,
    Rear,
    Left,
    Right,
}

public enum NCHitLocationSource : byte
{
    Scripted,
    Selected,
    Collision,
    Weighted,
}

public enum NCArmorLocation : byte
{
    Head,
    Face,
    Chest,
    Abdomen,
    Groin,
    ArmLeft,
    ArmRight,
    HandLeft,
    HandRight,
    LegLeft,
    LegRight,
    FootLeft,
    FootRight,
}
