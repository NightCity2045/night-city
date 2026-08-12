// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Reusable condition thresholds. Thresholds remain in YAML for balance and presentation tuning.
/// </summary>
[Prototype("ncArmorConditionProfile")]
public sealed partial class NCArmorConditionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<NCArmorConditionStageDefinition> Stages { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCArmorConditionStageDefinition
{
    [DataField(required: true)]
    public NCArmorConditionStage Stage { get; private set; }

    /// <summary>
    /// Inclusive fraction of MaxSP at which this stage begins, in the range 0..1.
    /// </summary>
    [DataField(required: true)]
    public float MinimumFraction { get; private set; }
}

/// <summary>
/// Static protection and coverage of one independently ablated section.
/// </summary>
[Prototype("ncArmorSection")]
public sealed partial class NCArmorSectionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Player-facing section name used by exact item examination.
    /// </summary>
    [DataField(required: true)]
    public LocId DisplayName { get; private set; } = default!;

    [DataField(required: true)]
    public List<ProtoId<NCTargetZonePrototype>> CoveredZones { get; private set; } = [];

    [DataField(required: true)]
    public FixedPoint2 MaxSp { get; private set; }

    /// <summary>
    /// An impact class not listed here ignores this section entirely.
    /// </summary>
    [DataField(required: true)]
    public List<NCArmorDamageClass> ApplicableDamageClasses { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCArmorConditionProfilePrototype> ConditionProfile { get; private set; }

    /// <summary>
    /// Stable grouping key for repair and future feedback. It never causes SP to stack.
    /// </summary>
    [DataField(required: true)]
    public string AblationGroup { get; private set; } = default!;

    [DataField]
    public bool Rigid { get; private set; }

    [DataField]
    public bool Sealed { get; private set; }

    /// <summary>
    /// Optional field-repair contract. Missing means that this section requires a specialized workflow.
    /// </summary>
    [DataField]
    public ProtoId<NCArmorRepairProfilePrototype>? RepairProfile { get; private set; }
}

/// <summary>
/// Composition of one armor item. Runtime SP is stored on the entity instance, not this prototype.
/// </summary>
[Prototype("ncArmorProfile")]
public sealed partial class NCArmorProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<ProtoId<NCArmorSectionPrototype>> Sections { get; private set; } = [];
}

/// <summary>
/// Data-defined limits for restoring one armor section outside a workshop.
/// </summary>
[Prototype("ncArmorRepairProfile")]
public sealed partial class NCArmorRepairProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// At least one capability on the used repair tool must match this list.
    /// </summary>
    [DataField(required: true)]
    public List<string> AcceptedCapabilities { get; private set; } = [];

    [DataField(required: true)]
    public FixedPoint2 RepairPerUse { get; private set; }

    /// <summary>
    /// Highest fraction of MaxSP reachable with this field repair profile.
    /// </summary>
    [DataField(required: true)]
    public float MaximumRepairFraction { get; private set; }

    [DataField]
    public bool CanRepairDestroyed { get; private set; }
}
