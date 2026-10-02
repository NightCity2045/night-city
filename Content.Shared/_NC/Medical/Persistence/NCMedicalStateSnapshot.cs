// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Persistence;

/// <summary>
/// Published versions for the semantic NC medical persistence contract.
/// A version is advanced only when the migration entry point can interpret the previous representation.
/// </summary>
public static class NCMedicalSnapshotVersions
{
    public const ushort InitialSemanticGraph = 1;
    public const ushort StableOrganIdentity = 2;
    public const ushort LongitudinalWounds = 3;
    public const ushort CyberwareGraph = 4;
    public const ushort PharmacologyGraph = 5;
    public const ushort SystemicSepsis = 6;
    public const ushort TransplantRejection = 7;
    public const ushort WoundVisualization = 8;
    public const ushort CavityFluidLedger = 9;
    public const ushort SpatialSurgery = 10;
    public const ushort Current = SpatialSurgery;
}

/// <summary>
/// Stable persistence namespace for player characters. Runtime entity identity is never persisted.
/// </summary>
public enum NCMedicalStableOwnerKind : byte
{
    Unassigned,
    PlayerCharacter,
    PersistentActor,
}

/// <summary>
/// Bounded, semantic state for one authoritative human body. Runtime entities, network identities,
/// active actions and scheduler bookkeeping are deliberately outside this contract.
/// </summary>
[DataDefinition]
public sealed partial class NCMedicalStateSnapshot
{
    [DataField]
    public ushort Version = NCMedicalSnapshotVersions.Current;

    /// <summary>
    /// Monotonic server time at capture. Absolute CurTime fields are shifted by the difference between
    /// this anchor and the new runtime anchor, so offline time never advances acute physiology.
    /// </summary>
    [DataField]
    public TimeSpan CapturedAt;

    [DataField(required: true)]
    public ProtoId<NCAnatomyProfilePrototype> AnatomyProfile;

    [DataField]
    public List<ProtoId<OrganCategoryPrototype>> MissingOrganCategories = [];

    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, NCOrganMedicalStateSnapshot> Organs = new();

    [DataField]
    public List<NCCyberwareStateSnapshot> CyberwareModules = [];

    [DataField]
    public NCWoundStateSnapshot? Wounds;

    [DataField]
    public NCBleedingStateSnapshot? Bleeding;

    [DataField]
    public NCTreatmentStateSnapshot? Treatment;

    [DataField]
    public NCEmergencyCareStateSnapshot? EmergencyCare;

    [DataField]
    public NCSystemicStateSnapshot? Systemic;

    [DataField]
    public NCPharmacologyStateSnapshot? Pharmacology;

    [DataField(required: true)]
    public NCCirculationStateSnapshot Circulation = new();

    [DataField(required: true)]
    public NCPhysiologyStateSnapshot Physiology = new();
}

/// <summary>
/// Mutable medical state of a live organ, keyed by its typed organ category in the parent snapshot.
/// Nullable sections preserve the component shape declared by the anatomy prototype.
/// </summary>
[DataDefinition]
public sealed partial class NCOrganMedicalStateSnapshot
{
    /// <summary>
    /// Exact entity prototype currently materializing this category. This preserves transplanted and
    /// cybernetic replacements instead of silently recreating InitialBody defaults after a restart.
    /// </summary>
    [DataField]
    public string EntityPrototype = string.Empty;

    [DataField]
    public Guid StableOrganId;

    [DataField]
    public NCMedicalStableOwnerKind DonorKind;

    [DataField]
    public string DonorKey = string.Empty;

    [DataField]
    public string CyberwareIdentity = string.Empty;

    [DataField]
    public NCOrganIntegrityStateSnapshot? Integrity;

    [DataField]
    public NCOrganFunctionStateSnapshot? Function;

    [DataField]
    public NCBoneStateSnapshot? Bone;

    [DataField]
    public NCTendonStateSnapshot? Tendon;

    [DataField]
    public NCJointStateSnapshot? Joint;

    [DataField]
    public NCNerveStateSnapshot? Nerve;

    [DataField]
    public NCSurgicalSiteStateSnapshot? SurgicalSite;

    [DataField]
    public NCCavityFluidStateSnapshot? Cavity;

    [DataField]
    public NCCyberwareStateSnapshot? Cyberware;

    [DataField]
    public NCTransplantRejectionStateSnapshot? Rejection;
}

[DataDefinition]
public sealed partial class NCTransplantRejectionStateSnapshot
{
    [DataField(required: true)]
    public ProtoId<NCTransplantRejectionProfilePrototype> Profile;

    [DataField]
    public bool Initialized;

    [DataField]
    public NCMedicalStableOwnerKind DonorKind;

    [DataField]
    public string DonorKey = string.Empty;

    [DataField]
    public NCMedicalStableOwnerKind RecipientKind;

    [DataField]
    public string RecipientKey = string.Empty;

    [DataField]
    public float Burden;

    [DataField]
    public NCTransplantRejectionStage Stage;
}

[DataDefinition]
public sealed partial class NCCavityFluidStateSnapshot
{
    [DataField] public NCBodyCavity Cavity;
    [DataField] public float BloodVolume;
    [DataField] public float OverflowBloodVolume;
    [DataField] public float MaximumBloodVolume;
    [DataField] public float PressureFullScaleVolume;
    [DataField] public float CompressionFullScaleVolume;
    [DataField] public float DrainRate;
}

[DataDefinition]
public sealed partial class NCOrganIntegrityStateSnapshot
{
    [DataField] public float MaxIntegrity;
    [DataField] public float CurrentIntegrity;
    [DataField] public float FunctionalEfficiency;
    [DataField] public bool Recoverable;
    [DataField] public float Necrosis;
    [DataField] public NCAnatomicalMaterialKind Material;
}

[DataDefinition]
public sealed partial class NCOrganFunctionStateSnapshot
{
    [DataField] public NCOrganFunctionType FunctionType;
    [DataField] public float BaseCapacity;
    [DataField] public float CurrentCapacity;
    [DataField] public float ImpairedThreshold;
    [DataField] public float FailingThreshold;
    [DataField] public float NonFunctionalThreshold;
    [DataField] public bool Vital;
    [DataField] public bool Recoverable;
    [DataField] public float Necrosis;
    [DataField] public float PerfusionDemand;
    [DataField] public float OxygenDemand;
}

[DataDefinition]
public sealed partial class NCBoneStateSnapshot
{
    [DataField] public NCBoneState State;
    [DataField] public float Stability;
    [DataField] public float Displacement;
    [DataField] public bool OpenFracture;
    [DataField] public float SecondaryDamageRisk;
    [DataField] public bool RequiresFixation;
    [DataField] public bool Splinted;
    [DataField] public TimeSpan SplintedAt;
}

[DataDefinition]
public sealed partial class NCTendonStateSnapshot
{
    [DataField] public float Integrity;
    [DataField] public bool Ruptured;
    [DataField] public float MovementContribution;
    [DataField] public float GripContribution;
    [DataField] public bool SurgicallyRepairable;
}

[DataDefinition]
public sealed partial class NCJointStateSnapshot
{
    [DataField] public float Stability;
    [DataField] public bool Dislocated;
    [DataField] public float MovementRestriction;
    [DataField] public bool Reduced;
}

[DataDefinition]
public sealed partial class NCNerveStateSnapshot
{
    [DataField] public float NeuralIntegrity;
    [DataField] public float Sensitivity;
    [DataField] public float MotorFunction;
    [DataField] public bool CyberprostheticInterface;
}

/// <summary>
/// Persistent surgical-site preparation. ActiveProcedure is intentionally absent because it identifies
/// an unsaved runtime session; restore always resumes from a safe, session-free site.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgicalSiteStateSnapshot
{
    [DataField] public NCSurgicalAccessDepth AccessDepth;
    [DataField] public bool BleedingControlled;
    [DataField] public bool BoneCasingOpen;
    [DataField] public bool TissuesRetracted;
    [DataField] public float Contamination;
    [DataField] public List<string> TemporaryDevices = [];
    [DataField] public List<NCSurgicalIncisionStateSnapshot> Incisions = [];
}

[DataDefinition]
public sealed partial class NCSurgicalIncisionStateSnapshot
{
    [DataField] public Guid IncisionId;
    [DataField] public Guid? WoundId;
    [DataField] public List<Guid> LinkedWoundIds = [];
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> TargetZone;
    [DataField] public NCWoundSurface Surface;
    [DataField] public List<Vector2> Polyline = [];
    [DataField] public float Depth;
    [DataField] public float Width;
    [DataField] public float EdgeSeparation;
    [DataField] public float Contamination;
    [DataField] public bool Sutured;
    [DataField] public bool BoneCasingOpen;
    [DataField] public NCActionResolutionId SourceResolutionId;
    [DataField] public TimeSpan CreatedAt;
    [DataField] public TimeSpan ModifiedAt;
}

[DataDefinition]
public sealed partial class NCWoundStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCWoundSystemProfilePrototype> Profile;
    [DataField] public Dictionary<Guid, NCWoundRecordSnapshot> Records = new();
    [DataField] public List<NCScarRecordSnapshot> Scars = [];
    [DataField] public Dictionary<ProtoId<NCTargetZonePrototype>, float> OverflowTraumaByZone = new();
    [DataField] public float OverflowPain;
}

[DataDefinition]
public sealed partial class NCWoundRecordSnapshot
{
    [DataField] public Guid WoundId;
    [DataField(required: true)] public ProtoId<NCWoundPrototype> Prototype;
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> TargetZone;
    [DataField(required: true)] public ProtoId<OrganCategoryPrototype> RegionCategory;
    [DataField] public ProtoId<DamageTypePrototype>? PrimaryDamageType;
    [DataField] public NCWoundType WoundType;
    [DataField] public NCTissueDepth TissueDepth;
    [DataField] public NCWoundSeverity Severity;
    [DataField] public NCWoundStage Stage;
    [DataField] public NCWoundClosureState ClosureState;
    [DataField] public NCWoundTreatmentFlags TreatmentFlags;
    [DataField] public FixedPoint2 IntegrityDamage;
    [DataField] public float Area;
    [DataField] public float ExternalBleedRate;
    [DataField] public float InternalBleedRate;
    [DataField] public float InitialExternalBleedRate;
    [DataField] public float InitialInternalBleedRate;
    [DataField] public NCBleedKind PrimaryBleedKind;
    [DataField] public float Pain;
    [DataField] public float InitialPain;
    [DataField] public float Contamination;
    [DataField] public float Infection;
    [DataField] public NCInfectionStage InfectionStage;
    [DataField] public float Necrosis;
    [DataField] public float HealingProgress;
    [DataField] public float PeakHealingProgress;
    [DataField] public float ReopenStress;
    [DataField] public float ScarRisk;
    [DataField] public Guid? EmbeddedObjectId;
    [DataField] public EntProtoId? EmbeddedObjectEntityPrototype;
    [DataField] public EntProtoId? SourceProjectilePrototype;
    [DataField] public Guid? ExitWoundId;
    /// <summary>
    /// Legacy migration slot. New captures always write an empty value and restore ignores it so combat-event
    /// provenance remains round-local instead of entering character storage.
    /// </summary>
    [DataField] public Guid ForensicResolutionId;
    [DataField] public byte VisualX;
    [DataField] public byte VisualY;
    [DataField] public byte VisualDirection;
    [DataField] public NCWoundSurface Surface;
    [DataField] public TimeSpan CreatedAt;
    [DataField] public TimeSpan ModifiedAt;
    [DataField] public TimeSpan Age;
}

[DataDefinition]
public sealed partial class NCScarRecordSnapshot
{
    [DataField] public Guid ScarId;
    [DataField] public Guid SourceWoundId;
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> TargetZone;
    [DataField(required: true)] public ProtoId<OrganCategoryPrototype> RegionCategory;
    [DataField] public NCWoundType WoundType;
    [DataField] public NCWoundSeverity OriginalSeverity;
    [DataField] public float Area;
    [DataField] public float Disfigurement;
    [DataField] public float Contracture;
    [DataField] public byte VisualX;
    [DataField] public byte VisualY;
    [DataField] public byte VisualDirection;
    [DataField] public NCWoundSurface Surface;
    [DataField] public TimeSpan CreatedAt;
}

[DataDefinition]
public sealed partial class NCBleedingStateSnapshot
{
    [DataField] public List<NCBleedingSourceStateSnapshot> Sources = [];
    [DataField] public float OverflowExternalRate;
    [DataField] public float OverflowInternalRate;
    [DataField] public Dictionary<ProtoId<OrganCategoryPrototype>, float> OverflowInternalRatesByRegion = new();
    [DataField] public float InternalBloodLoss;
    [DataField] public float UnlocalizedInternalBloodVolume;
}

[DataDefinition]
public sealed partial class NCBleedingSourceStateSnapshot
{
    [DataField] public Guid SourceId;
    [DataField] public Guid WoundId;
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> TargetZone;
    [DataField(required: true)] public ProtoId<OrganCategoryPrototype> RegionCategory;
    [DataField] public NCBleedingChannel Channel;
    [DataField] public float BaseRate;
    [DataField] public float CurrentRate;
    [DataField] public bool PressureApplied;
    [DataField] public bool Bandaged;
    [DataField] public bool Clamped;
    [DataField] public bool Tourniqueted;
    [DataField] public bool Cauterized;
}

[DataDefinition]
public sealed partial class NCTreatmentStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCTreatmentProfilePrototype> Profile;
    [DataField] public Dictionary<Guid, NCWoundTreatmentStateSnapshot> WoundTreatments = new();
    [DataField] public Dictionary<ProtoId<NCTargetZonePrototype>, NCTourniquetStateSnapshot> Tourniquets = new();
}

[DataDefinition]
public sealed partial class NCWoundTreatmentStateSnapshot
{
    [DataField] public Guid WoundId;
    [DataField] public NCWoundTreatmentFlags AppliedFlags;
    [DataField] public TimeSpan PressureAppliedAt;
    [DataField] public TimeSpan BandagedAt;
    [DataField] public TimeSpan HemostaticAppliedAt;
    [DataField] public float BandageAbsorptionRemaining;
    [DataField] public float BandageSterility;
    [DataField] public TimeSpan ModifiedAt;
}

[DataDefinition]
public sealed partial class NCTourniquetStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> RootZone;
    [DataField] public List<Guid> CoveredWoundIds = [];
    [DataField] public bool Active;
    [DataField] public TimeSpan AppliedAt;
    [DataField] public TimeSpan IschemiaRiskAt;
    [DataField] public TimeSpan CriticalIschemiaAt;
    [DataField] public TimeSpan RemovedAt;
    [DataField] public TimeSpan ReboundEndsAt;
    [DataField] public bool ControlledRelease;
}

/// <summary>
/// Persistent physiological support. Processed action IDs and emergency audit entries are intentionally
/// excluded so retries from a previous runtime cannot become authorization after a load boundary.
/// </summary>
[DataDefinition]
public sealed partial class NCEmergencyCareStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCEmergencyCareProfilePrototype> Profile;
    [DataField] public TimeSpan CprRemaining;
    [DataField] public TimeSpan OxygenRemaining;
    [DataField] public TimeSpan VentilationRemaining;
    [DataField] public float CrystalloidVolume;
}

[DataDefinition]
public sealed partial class NCSystemicStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCSystemicPhysiologyProfilePrototype> Profile;
    [DataField] public bool Initialized;
    [DataField] public Dictionary<NCSystemicChannel, float> Burdens = new();
    [DataField] public bool ShockableArrestLatched;
}

[DataDefinition]
public sealed partial class NCCirculationStateSnapshot
{
    [DataField(required: true)] public ProtoId<NCPhysiologyProfilePrototype> Profile;
    [DataField] public bool Initialized;
    [DataField] public float BloodVolume;
    [DataField] public float EffectiveCirculatingVolume;
    [DataField] public float CardiacOutput;
    [DataField] public float Perfusion;
    [DataField] public NCBloodPressureState BloodPressureState;
    [DataField] public NCHeartRhythm HeartRhythm;
    [DataField] public string TransfusionCompatibility = string.Empty;
}

[DataDefinition]
public sealed partial class NCPhysiologyStateSnapshot
{
    [DataField] public float Oxygenation;
    [DataField] public float TotalPain;
    [DataField] public NCPainLevel PainLevel;
    [DataField] public float Shock;
    [DataField] public NCShockStage ShockStage;
    [DataField] public NCConsciousnessState Consciousness;
    [DataField] public NCPhysiologicalCondition Condition;
    [DataField] public float BrainFunction;
    [DataField] public TimeSpan TimeBelowCriticalPerfusion;
    [DataField] public TimeSpan TimeInClinicalDeath;
    [DataField] public bool Irreversible;
}

/// <summary>
/// Stable organ provenance carried through transplant and restart boundaries.
/// </summary>
[RegisterComponent]
public sealed partial class NCOrganPersistenceIdentityComponent : Component
{
    [DataField]
    public Guid StableOrganId;

    [DataField]
    public NCMedicalStableOwnerKind DonorKind;

    [DataField]
    public string DonorKey = string.Empty;

    [DataField]
    public string CyberwareIdentity = string.Empty;
}
