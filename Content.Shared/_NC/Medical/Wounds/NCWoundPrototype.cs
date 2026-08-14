// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Systemic;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Wounds;

/// <summary>
/// Data-driven conversion from a trauma mechanism into one local wound record.
/// </summary>
[Prototype("ncWound")]
public sealed partial class NCWoundPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCWoundType WoundType { get; private set; }

    [DataField(required: true)]
    public NCTissueDepth TissueDepth { get; private set; }

    [DataField]
    public bool CanMergeMinor { get; private set; }

    [DataField]
    public float AreaPerDamage { get; private set; } = 1f;

    [DataField]
    public float ExternalBleedPerDamage { get; private set; }

    [DataField]
    public float InternalBleedPerDamage { get; private set; }

    [DataField]
    public NCWoundSeverity? ArterialFromSeverity { get; private set; }

    [DataField]
    public float ArterialRateMultiplier { get; private set; } = 1f;

    [DataField]
    public float PainPerDamage { get; private set; } = 1f;

    [DataField]
    public float ContaminationMultiplier { get; private set; } = 1f;

    [DataField]
    public float ScarRiskPerDamage { get; private set; }

    [DataField]
    public float NaturalHealingMultiplier { get; private set; } = 1f;

    [DataField]
    public float InfectionSusceptibility { get; private set; } = 1f;

    [DataField]
    public float ReopenSusceptibility { get; private set; } = 1f;

    /// <summary>
    /// Fraction of the region integrity damage that ordinary healing may restore. Bone, tendon, nerve and
    /// contained-organ injuries remain owned by their dedicated treatment paths.
    /// </summary>
    [DataField]
    public float NaturalIntegrityRecoveryFraction { get; private set; }

    [DataField]
    public float StatusDamageMultiplier { get; private set; } = 1f;

    [DataField]
    public float RegionIntegrityDamageMultiplier { get; private set; } = 1f;

    [DataField]
    public float ContainedStructureDamageMultiplier { get; private set; }

    [DataField]
    public float ContainedOrganDamageMultiplier { get; private set; }

    [DataField]
    public float FunctionLossPerDamage { get; private set; } = 0.01f;

    [DataField]
    public NCWoundSeverity? StructuralDamageFromSeverity { get; private set; }

    [DataField]
    public NCWoundSeverity? ContainedOrganDamageFromSeverity { get; private set; }

    [DataField]
    public int ContainedTargetCount { get; private set; } = 1;

    [DataField]
    public NCWoundSeverity? TendonDamageFromSeverity { get; private set; }

    [DataField]
    public float TendonIntegrityLossPerDamage { get; private set; }

    [DataField]
    public float TendonRuptureIntegrityThreshold { get; private set; }

    [DataField]
    public NCWoundSeverity? NerveDamageFromSeverity { get; private set; }

    [DataField]
    public float NerveIntegrityLossPerDamage { get; private set; }

    [DataField]
    public float NerveMotorLossPerDamage { get; private set; }

    [DataField]
    public NCWoundSeverity? JointDamageFromSeverity { get; private set; }

    [DataField]
    public float JointStabilityLossPerDamage { get; private set; }

    [DataField]
    public float JointDislocationStabilityThreshold { get; private set; }
}

/// <summary>
/// Human-authoritative wound catalogue and bounded-record policy.
/// </summary>
[Prototype("ncWoundSystemProfile")]
public sealed partial class NCWoundSystemProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCTraumaMechanism, ProtoId<NCWoundPrototype>> Wounds { get; private set; } = new();

    /// <summary>
    /// Inclusive upper bounds for Minor, Moderate, Severe and Critical trauma.
    /// Values above the fourth threshold are Catastrophic.
    /// </summary>
    [DataField(required: true)]
    public List<float> SeverityThresholds { get; private set; } = [];

    /// <summary>
    /// Inclusive lower bounds for target-doll levels 1 through 8.
    /// </summary>
    [DataField(required: true)]
    public List<float> StatusDamageThresholds { get; private set; } = [];

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, byte> SeverityStatusLevels { get; private set; } = new();

    [DataField]
    public int MaxActiveWounds { get; private set; } = 32;

    [DataField]
    public int ProcessedImpactCapacity { get; private set; } = 128;

    [DataField]
    public float MergeContaminationTolerance { get; private set; } = 0.05f;

    [DataField(required: true)]
    public ProtoId<NCWoundProgressionProfilePrototype> ProgressionProfile { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCImpactBloodProfilePrototype> ImpactBloodProfile { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, NCBoneState> BoneStateBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public float BoneStabilityLossPerDamage { get; private set; }

    [DataField(required: true)]
    public NCBoneState DisplacementFromState { get; private set; }

    [DataField(required: true)]
    public NCBoneState FixationFromState { get; private set; }
}

/// <summary>
/// Deterministic longitudinal wound policy. All rates and thresholds are prototype data; the server never
/// performs RED checks, skill rolls or random infection/scar decisions.
/// </summary>
[Prototype("ncWoundProgressionProfile")]
public sealed partial class NCWoundProgressionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, TimeSpan> HealingDurationBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundClosureState, float> ClosureHealingMultipliers { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, float> ReopenStressThresholds { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, float> ImpactReopenStressBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, float> InfectionSeverityMultipliers { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, float> ScarContractureBySeverity { get; private set; } = new();

    /// <summary>
    /// Inclusive infection-load thresholds for Localized, Deep, Organ, Systemic and Sepsis.
    /// Contaminated is derived from contamination load before infection establishes.
    /// </summary>
    [DataField(required: true)]
    public List<float> InfectionStageThresholds { get; private set; } = [];

    [DataField(required: true)]
    public TimeSpan FreshToOpenDelay { get; private set; }

    [DataField(required: true)]
    public NCWoundSeverity NaturalClosureMaximumSeverity { get; private set; }

    [DataField(required: true)]
    public float NaturalClosureProgress { get; private set; }

    [DataField(required: true)]
    public float OpenContaminationPerSecond { get; private set; }

    [DataField(required: true)]
    public float SterileContaminationDecayPerSecond { get; private set; }

    [DataField(required: true)]
    public float InfectionContaminationThreshold { get; private set; }

    [DataField(required: true)]
    public float InfectionGrowthPerSecond { get; private set; }

    [DataField(required: true)]
    public float DebridementInfectionRecoveryPerSecond { get; private set; }

    [DataField(required: true)]
    public float AntibioticRecoveryPerPowerPerSecond { get; private set; }

    [DataField(required: true)]
    public float ImmunosuppressionInfectionMultiplier { get; private set; }

    [DataField(required: true)]
    public float EmbeddedObjectInfectionMultiplier { get; private set; }

    [DataField(required: true)]
    public float ClosedInfectionMultiplier { get; private set; }

    [DataField(required: true)]
    public NCInfectionStage HealingBlockedAtInfectionStage { get; private set; }

    [DataField(required: true)]
    public NCInfectionStage NecrosisStartsAtInfectionStage { get; private set; }

    [DataField(required: true)]
    public float InfectionNecrosisPerSecond { get; private set; }

    [DataField(required: true)]
    public float NecroticThreshold { get; private set; }

    [DataField(required: true)]
    public float HealingBleedReductionFraction { get; private set; }

    [DataField(required: true)]
    public float HealingPainReductionFraction { get; private set; }

    [DataField(required: true)]
    public float MinimumHealingPerfusion { get; private set; }

    [DataField(required: true)]
    public float ReopenHealingRetention { get; private set; }

    [DataField(required: true)]
    public float ReopenBleedFraction { get; private set; }

    [DataField(required: true)]
    public float ReopenContamination { get; private set; }

    [DataField(required: true)]
    public float ReopenScarRisk { get; private set; }

    [DataField(required: true)]
    public float ReopenStressDecayPerSecond { get; private set; }

    [DataField(required: true)]
    public float MovementStressPerTile { get; private set; }

    [DataField(required: true)]
    public float MaximumPendingMovementDistance { get; private set; }

    [DataField(required: true)]
    public TimeSpan PersistenceCheckpointInterval { get; private set; }

    [DataField(required: true)]
    public float ScarCreationThreshold { get; private set; }

    [DataField(required: true)]
    public NCWoundSeverity GuaranteedScarFromSeverity { get; private set; }

    [DataField(required: true)]
    public int MaximumScars { get; private set; }

    [DataField(required: true)]
    public float IschemiaIntegrityDamagePerSecond { get; private set; }

    [DataField(required: true)]
    public float CriticalIschemiaIntegrityDamagePerSecond { get; private set; }

    [DataField(required: true)]
    public float IschemiaNerveDamagePerSecond { get; private set; }

    [DataField(required: true)]
    public float CriticalIschemiaNerveDamagePerSecond { get; private set; }

    [DataField(required: true)]
    public float IschemiaNecrosisPerSecond { get; private set; }

    [DataField(required: true)]
    public float CriticalIschemiaNecrosisPerSecond { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCSystemicDamageProfilePrototype> SepsisSystemicProfile { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCInfectionStage, float> SepsisExposurePerSecondByStage { get; private set; } = new();

    [DataField(required: true)]
    public float SepsisAntibioticRecoveryPerPowerPerSecond { get; private set; }
}
