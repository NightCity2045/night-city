// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>
/// One physical implant bay declared by a concrete node of the live human organ graph.
/// </summary>
[Prototype("ncCyberwareSlot")]
public sealed partial class NCCyberwareSlotPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;

    [DataField]
    public LocId? NameId { get; private set; }

    [DataField(required: true)]
    public List<ProtoId<OrganCategoryPrototype>> HostCategories { get; private set; } = [];

    /// <summary>Accepted only when inspecting old saved hosts, never for a new installation.</summary>
    [DataField]
    public List<ProtoId<OrganCategoryPrototype>> LegacyHostCategories { get; private set; } = [];

    [DataField(required: true)]
    public float Capacity { get; private set; }

    /// <summary>Horizontal atlas position of this bay's regional surgical connectors.</summary>
    [DataField]
    public float RegionalSurgeryPositionX { get; private set; } = .5f;
}

/// <summary>
/// Consumable service outcome. Surgery and repair gameplay select this profile explicitly; the declared
/// physical restoration is deterministic.
/// </summary>
[Prototype("ncCyberwareRepairProfile")]
public sealed partial class NCCyberwareRepairProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;

    [DataField(required: true)]
    public float IntegrityPerUnit { get; private set; }

    [DataField]
    public float PowerPerUnit { get; private set; }

    [DataField]
    public float CoolingPerUnit { get; private set; }

    [DataField]
    public float NeuralIntegrityPerUnit { get; private set; }

    [DataField(required: true)]
    public float MaximumUnitsPerAction { get; private set; } = 1f;

    [DataField]
    public float MaximumUnitsPerTarget { get; private set; } = 1f;

    [DataField]
    public int MaximumTargetsPerAction { get; private set; } = 8;

    [DataField]
    public NCCyberwareServiceDistribution Distribution { get; private set; }
}

/// <summary>
/// Immutable balance and compatibility for one implant or cybernetic anatomical replacement.
/// All thresholds and rates are content values; the server system only applies their deterministic rules.
/// </summary>
[Prototype("ncCyberware")]
public sealed partial class NCCyberwarePrototype : IPrototype
{
    [DataField]
    public List<ProtoId<Content.Shared._NC.Medical.Humanity.NCHumanityPackagePrototype>> HumanityPackages { get; private set; } = [];

    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;

    [DataField]
    public LocId? NameId { get; private set; }

    [DataField(required: true)]
    public string Manufacturer { get; private set; } = default!;

    /// <summary>
    /// Shared presentation icon used by diagnostics and other capability-filtered medical interfaces.
    /// Physical entity prototypes use the same RSI state, keeping inventory and UI identity consistent.
    /// </summary>
    [DataField(required: true)]
    public SpriteSpecifier Icon { get; private set; } = default!;

    /// <summary>
    /// Optional host-specific presentation, used by mirrored replacement limbs without duplicating their
    /// gameplay profile. Diagnostics resolves this only from the already disclosed host category.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, SpriteSpecifier> HostIcons { get; private set; } = [];

    [DataField(required: true)]
    public NCCyberwareForm Form { get; private set; }

    /// <summary>One independent neural foundation required for operation, not physical containment.</summary>
    [DataField]
    public ProtoId<NCCyberwarePrototype>? RequiredNeuralInterface { get; private set; }

    [DataField]
    public ProtoId<NCCyberwarePrototype>? RequiredSupportProfile { get; private set; }

    [DataField]
    public int RequiredSupportCount { get; private set; }

    [DataField]
    public NCAnatomicalMaterialKind Material { get; private set; } = NCAnatomicalMaterialKind.Mechanical;

    [DataField]
    public List<NCSkeletalSurgeryStep> SkeletalSurgery { get; private set; } = [];

    /// <summary>Passive regional armor whose SP is derived from persisted implant integrity.</summary>
    [DataField] public bool DermalArmor { get; private set; }

    [DataField]
    public bool RequiresSkeletalLoadTest { get; private set; }

    [DataField] public bool RequiresFactoryPreparation { get; private set; }
    [DataField] public float FactoryTestEnergy { get; private set; } = 1f;
    [DataField] public float FactoryTestSeconds { get; private set; } = 5f;
    [DataField] public float FactoryMinimumEfficiency { get; private set; } = .9f;

    [DataField]
    public bool SkeletalEnhancement { get; private set; }

    /// <summary>Percentage-point allowance against a worn hardsuit's own clothing penalty only.</summary>
    [DataField]
    public float HardsuitPenaltyAllowance { get; private set; }

    [DataField]
    public float BoneTraumaMultiplier { get; private set; } = 1f;

    [DataField]
    public bool VulnerableToEmp { get; private set; } = true;

    [DataField]
    public List<ProtoId<NCCyberwareSlotPrototype>> CompatibleSlots { get; private set; } = [];

    [DataField(required: true)]
    public float Size { get; private set; }

    [DataField(required: true)]
    public float MaximumIntegrity { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCyberwareRepairProfilePrototype> RepairProfile { get; private set; }

    [DataField]
    public List<ProtoId<NCCyberwareRepairProfilePrototype>> AuxiliaryServiceProfiles { get; private set; } = [];

    [DataField]
    // Legacy capacities are retained only to validate and migrate pre-12 snapshots.
    public float PowerCapacity { get; private set; }

    [DataField]
    public float StartingPowerFraction { get; private set; } = 1f;

    [DataField]
    public float MinimumOperationalPower { get; private set; }

    [DataField]
    public float IdlePowerPerSecond { get; private set; }

    [DataField]
    public float LoadedPowerPerSecond { get; private set; }

    [DataField]
    public bool RequiresPower { get; private set; } = true;

    [DataField]
    public NCCyberwarePowerPriority PowerPriority { get; private set; } = NCCyberwarePowerPriority.Professional;

    [DataField(required: true)]
    public float HeatCapacity { get; private set; }

    [DataField]
    public float IdleHeatPerSecond { get; private set; }

    [DataField]
    public float LoadedHeatPerSecond { get; private set; }

    [DataField]
    public float PassiveCoolingPerSecond { get; private set; }

    [DataField(required: true)]
    public float HeatDeratingThreshold { get; private set; }

    [DataField(required: true)]
    public float HeatShutdownThreshold { get; private set; }

    [DataField(required: true)]
    public float HeatDamageThreshold { get; private set; }

    [DataField]
    public float HeatDamagePerSecond { get; private set; }

    [DataField]
    public float EmpResistance { get; private set; }

    [DataField]
    public float EmpSurgeDamagePerEnergy { get; private set; }

    [DataField]
    public float MaximumEmpSurgeDamage { get; private set; }

    [DataField]
    public float EmpHeatPerEnergy { get; private set; }

    [DataField]
    public float MaximumEmpHeat { get; private set; }

    [DataField]
    public float EmpDisableDurationMultiplier { get; private set; } = 1f;

    [DataField]
    public float NeuralDamagePerEmpEnergy { get; private set; }

    [DataField]
    public NCCyberwareNeuralRequirement NeuralRequirement { get; private set; }

    [DataField]
    public float NeuralDegradedThreshold { get; private set; } = 0.7f;

    [DataField]
    public float NeuralMinimumOperationalIntegrity { get; private set; } = 0.2f;

    [DataField(required: true)]
    public TimeSpan UpdateInterval { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public List<string> Capabilities { get; private set; } = [];

    [DataField]
    public ProtoId<NCCyberwareAssemblyPrototype>? Assembly { get; private set; }

    [DataField]
    public bool RequiresSeparateCalibration { get; private set; }

    [DataField]
    public float CalibrationMinimumEfficiency { get; private set; } = 0.5f;

    [DataField]
    public float CalibrationEnergy { get; private set; } = 1f;
}

public enum NCCyberwareForm : byte
{
    Implant,
    OrganReplacement,
    LimbReplacement,
}

public enum NCCyberwareNeuralRequirement : byte
{
    None,
    Optional,
    Required,
}

public enum NCCyberwareServiceDistribution : byte
{
    MostDamagedFirst,
    HottestFirst,
    LowestPowerFirst,
    StableIdentity,
}
