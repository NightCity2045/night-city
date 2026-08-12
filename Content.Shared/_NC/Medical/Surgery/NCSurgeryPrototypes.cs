// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// A physical capability supplied by a surgical tool. It is deliberately unrelated to character skills.
/// </summary>
[Prototype("ncSurgeryToolCapability")]
public sealed partial class NCSurgeryToolCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;
}

/// <summary>
/// An observable capability supplied by a table, clinic or other explicitly bound facility.
/// </summary>
[Prototype("ncSurgeryFacilityCapability")]
public sealed partial class NCSurgeryFacilityCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;
}

/// <summary>
/// An explicit access scope. It represents authorization, never medical aptitude or a dice modifier.
/// </summary>
[Prototype("ncSurgeryAuthorization")]
public sealed partial class NCSurgeryAuthorizationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;
}

/// <summary>
/// One explicit surgery plan. Steps are resolved in list order; the server never scans every operation
/// to guess what a tool was meant to do.
/// </summary>
[Prototype("ncSurgeryProcedure")]
public sealed partial class NCSurgeryProcedurePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;

    [DataField]
    public string Description { get; private set; } = string.Empty;

    [DataField(required: true)]
    public List<ProtoId<NCAnatomyProfilePrototype>> CompatibleBodyProfiles { get; private set; } = [];

    [DataField]
    public List<ProtoId<NCSurgeryAuthorizationPrototype>> RequiredAuthorizations { get; private set; } = [];

    [DataField]
    public List<NCAnatomicalMaterialKind> CompatibleMaterials { get; private set; } =
        [NCAnatomicalMaterialKind.Organic, NCAnatomicalMaterialKind.Hybrid];

    [DataField]
    public List<ProtoId<OrganCategoryPrototype>> CompatibleTargetCategories { get; private set; } = [];

    [DataField]
    public NCSurgeryTargetRequirements TargetRequirements { get; private set; } =
        NCSurgeryTargetRequirements.Integrity;

    [DataField]
    public bool RequiresTargetWound { get; private set; }

    [DataField]
    public List<NCWoundType> CompatibleWoundTypes { get; private set; } = [];

    [DataField]
    public bool RequiresGraft { get; private set; }

    [DataField]
    public List<ProtoId<OrganCategoryPrototype>> CompatibleGraftCategories { get; private set; } = [];

    [DataField]
    public List<NCAnatomicalMaterialKind> CompatibleGraftMaterials { get; private set; } =
        [NCAnatomicalMaterialKind.Organic, NCAnatomicalMaterialKind.Hybrid];

    /// <summary>
    /// Observable pathology that must still be present when the procedure starts and before every step.
    /// These are deterministic hard gates, never character checks or random success modifiers.
    /// </summary>
    [DataField]
    public NCSurgeryTargetStateRequirement TargetState { get; private set; } = new();

    [DataField]
    public List<NCSurgicalAccessDepth> StartingAccessDepths { get; private set; } =
        [NCSurgicalAccessDepth.Closed, NCSurgicalAccessDepth.ClosedPostOp];

    [DataField]
    public NCSurgeryFacilityRequirement Facility { get; private set; } = new();

    [DataField]
    public NCSurgeryEnvironmentRequirement Environment { get; private set; } = new();

    [DataField]
    public NCSurgeryPatientRequirement Patient { get; private set; } = new();

    [DataField(required: true)]
    public List<ProtoId<NCSurgeryStepPrototype>> Steps { get; private set; } = [];

    [DataField]
    public List<NCSurgeryEffectDefinition> CompletionEffects { get; private set; } = [];

    [DataField]
    public List<NCSurgeryEffectDefinition> AbortEffects { get; private set; } = [];
}

/// <summary>
/// One deterministic transition. Hard requirements block the transition; ordered outcome rules select
/// an effect list from the server-observed condition snapshot without randomness.
/// </summary>
[Prototype("ncSurgeryStep")]
public sealed partial class NCSurgeryStepPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string Name { get; private set; } = default!;

    [DataField]
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField(required: true)]
    public NCSurgicalAccessDepth RequiredAccessDepth { get; private set; }

    [DataField]
    public List<NCSurgeryToolRequirement> Tools { get; private set; } = [];

    [DataField]
    public NCSurgeryFacilityRequirement Facility { get; private set; } = new();

    [DataField]
    public NCSurgeryEnvironmentRequirement Environment { get; private set; } = new();

    [DataField]
    public NCSurgeryPatientRequirement Patient { get; private set; } = new();

    /// <summary>
    /// Rules are checked in prototype order. They describe physical degraded conditions, not chance.
    /// </summary>
    [DataField]
    public List<NCSurgeryOutcomeRule> OutcomeRules { get; private set; } = [];

    [DataField]
    public NCSurgeryStepOutcome DefaultOutcome { get; private set; } = NCSurgeryStepOutcome.Success;

    [DataField]
    public List<NCSurgeryEffectDefinition> SuccessEffects { get; private set; } = [];

    [DataField]
    public List<NCSurgeryEffectDefinition> CompromisedEffects { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCSurgeryToolRequirement
{
    [DataField(required: true)]
    public ProtoId<NCSurgeryToolCapabilityPrototype> Capability { get; private set; }

    [DataField]
    public float MinimumQuality { get; private set; }

    [DataField]
    public float MinimumSterility { get; private set; }

    [DataField]
    public int ConsumedUses { get; private set; }
}

[DataDefinition]
public sealed partial class NCSurgeryFacilityRequirement
{
    [DataField]
    public bool Required { get; private set; }

    [DataField]
    public List<ProtoId<NCSurgeryFacilityCapabilityPrototype>> Capabilities { get; private set; } = [];

    [DataField]
    public float MinimumQuality { get; private set; }

    [DataField]
    public float MinimumSterility { get; private set; }

    [DataField]
    public bool RequirePower { get; private set; }

    [DataField]
    public bool RequireStableSurface { get; private set; }
}

[DataDefinition]
public sealed partial class NCSurgeryEnvironmentRequirement
{
    [DataField]
    public bool Required { get; private set; }

    [DataField]
    public float MinimumSterility { get; private set; }

    [DataField]
    public float MinimumLighting { get; private set; }

    [DataField]
    public float MaximumMotion { get; private set; } = 1f;

    [DataField]
    public float MaximumContamination { get; private set; } = 1f;
}

[DataDefinition]
public sealed partial class NCSurgeryPatientRequirement
{
    [DataField]
    public float MinimumOxygenation { get; private set; }

    [DataField]
    public NCShockStage MaximumShockStage { get; private set; } = NCShockStage.Refractory;

    [DataField]
    public List<NCConsciousnessState> AllowedConsciousness { get; private set; } = [];

    [DataField]
    public List<NCPhysiologicalCondition> AllowedConditions { get; private set; } = [];

    [DataField]
    public float MinimumAnalgesia { get; private set; }

    [DataField]
    public float MinimumSedation { get; private set; }

    [DataField]
    public float MinimumAnesthesia { get; private set; }

    [DataField]
    public float MaximumMovement { get; private set; } = 1f;

    [DataField]
    public bool RequireRestraintWhenConscious { get; private set; }

    [DataField]
    public bool RequireAirwayMonitoring { get; private set; }
}

/// <summary>
/// Hard, server-observed target state for a procedure. Nullable bounds mean that the procedure does not
/// constrain that structure. A target cannot become eligible merely because the client selected it.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgeryTargetStateRequirement
{
    [DataField]
    public float? MaximumIntegrityRatio { get; private set; }

    [DataField]
    public float? MaximumFunctionRatio { get; private set; }

    [DataField]
    public List<NCBoneState> AllowedBoneStates { get; private set; } = [];

    [DataField]
    public float? MaximumTendonIntegrity { get; private set; }

    [DataField]
    public float? MaximumNerveIntegrity { get; private set; }

    [DataField]
    public bool RequireActiveBleeding { get; private set; }

    [DataField]
    public bool RequireEmbeddedObject { get; private set; }

    [DataField]
    public float? MinimumWoundContamination { get; private set; }

    [DataField]
    public float? MinimumCavityBloodVolume { get; private set; }
}

/// <summary>
/// A deterministic branch predicate. All populated bounds must match; the first matching rule wins.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgeryOutcomeRule
{
    [DataField(required: true)]
    public NCSurgeryStepOutcome Outcome { get; private set; }

    [DataField]
    public float? MaximumToolQuality { get; private set; }

    [DataField]
    public float? MaximumToolSterility { get; private set; }

    [DataField]
    public float? MaximumFacilityQuality { get; private set; }

    [DataField]
    public float? MaximumFacilitySterility { get; private set; }

    [DataField]
    public float? MaximumEnvironmentSterility { get; private set; }

    [DataField]
    public float? MinimumEnvironmentContamination { get; private set; }

    [DataField]
    public float? MinimumPatientMovement { get; private set; }

    [DataField]
    public NCShockStage? MinimumShockStage { get; private set; }

    [DataField]
    public float? MinimumSiteContamination { get; private set; }
}

/// <summary>
/// A typed, inspectable mutation. Unused fields are ignored according to <see cref="Kind"/>.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgeryEffectDefinition
{
    [DataField(required: true)]
    public NCSurgeryEffectKind Kind { get; private set; }

    [DataField]
    public float Amount { get; private set; }

    [DataField]
    public bool Enabled { get; private set; } = true;

    [DataField]
    public NCSurgicalAccessDepth AccessDepth { get; private set; }

    [DataField]
    public NCBoneState BoneState { get; private set; }

    [DataField]
    public NCWoundTreatmentFlags TreatmentFlags { get; private set; }

    [DataField]
    public NCWoundClosureState ClosureState { get; private set; }

    [DataField]
    public NCSurgeryWoundScope WoundScope { get; private set; } = NCSurgeryWoundScope.TargetWound;

    [DataField]
    public NCSurgeryEffectTarget Target { get; private set; } = NCSurgeryEffectTarget.TargetRegion;
}

[Flags]
public enum NCSurgeryTargetRequirements : ushort
{
    None = 0,
    Integrity = 1 << 0,
    Function = 1 << 1,
    Bone = 1 << 2,
    Tendon = 1 << 3,
    Nerve = 1 << 4,
    Vascular = 1 << 5,
}

public enum NCSurgeryStepOutcome : byte
{
    Success,
    Compromised,
}

public enum NCSurgeryEffectKind : byte
{
    SetAccessDepth,
    AddSiteContamination,
    SetBleedingControlled,
    SetTissuesRetracted,
    SetBoneCasingOpen,
    AdjustIntegrity,
    AdjustOrganFunction,
    SetBoneState,
    SetBoneStability,
    AdjustTendonIntegrity,
    AdjustNerveIntegrity,
    AddWoundTreatmentFlags,
    SetWoundClosure,
    AdjustWoundContamination,
    ControlBleeding,
    RemoveEmbeddedObject,
    AdjustCavityBloodVolume,
    SetCavityDrainRate,
    DetachTargetOrgan,
    AttachGraft,
    RecordAutopsy,
}

public enum NCSurgeryWoundScope : byte
{
    TargetWound,
    AllWoundsInRegion,
}

public enum NCSurgeryEffectTarget : byte
{
    TargetRegion,
    Graft,
}
