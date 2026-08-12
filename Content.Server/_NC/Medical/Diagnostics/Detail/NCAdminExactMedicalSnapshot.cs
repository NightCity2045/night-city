// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared.Body;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Trusted server-only exact view. It is intentionally not NetSerializable and can retain wound identifiers.
/// </summary>
public sealed record NCAdminExactMedicalSnapshot(
    NCActionResolutionId ResolutionId,
    ushort SnapshotVersion,
    uint MedicalRevision,
    TimeSpan ObservedAt,
    float Oxygenation,
    float TotalPain,
    float Shock,
    float BloodVolume,
    float EffectiveCirculatingVolume,
    float Perfusion,
    float ExternalBleedRate,
    float InternalBleedRate,
    float InternalBloodLoss,
    NCConsciousnessState Consciousness,
    NCPhysiologicalCondition Condition,
    List<NCAdminExactWoundSummary> Wounds,
    List<NCAdminExactScarSummary> Scars,
    List<NCAdminExactOrganSummary> Organs,
    List<NCAdminExactCyberwareSummary> Cyberware,
    List<NCAdminExactDrugSummary> Drugs,
    NCMedicalSurgerySummary? Surgery);

public readonly record struct NCAdminExactWoundSummary(
    Guid WoundId,
    ProtoId<NCWoundPrototype> Prototype,
    ProtoId<NCTargetZonePrototype> Zone,
    ProtoId<OrganCategoryPrototype> Region,
    ProtoId<DamageTypePrototype>? DamageType,
    NCWoundType Type,
    NCTissueDepth Depth,
    NCWoundSeverity Severity,
    NCWoundStage Stage,
    NCWoundClosureState Closure,
    float IntegrityDamage,
    float ExternalBleedRate,
    float InternalBleedRate,
    float Pain,
    float Contamination,
    float Infection,
    NCInfectionStage InfectionStage,
    float Necrosis,
    float HealingProgress,
    Guid? EmbeddedObjectId,
    Guid ForensicResolutionId,
    TimeSpan CreatedAt,
    TimeSpan ModifiedAt);

public readonly record struct NCAdminExactScarSummary(
    Guid ScarId,
    Guid SourceWoundId,
    ProtoId<NCTargetZonePrototype> Zone,
    ProtoId<OrganCategoryPrototype> Region,
    NCWoundType Type,
    NCWoundSeverity OriginalSeverity,
    float Area,
    float Disfigurement,
    float Contracture,
    TimeSpan CreatedAt);

public readonly record struct NCAdminExactOrganSummary(
    ProtoId<OrganCategoryPrototype> Category,
    float CurrentIntegrity,
    float MaxIntegrity,
    float FunctionalEfficiency,
    NCOrganFunctionType? FunctionType,
    float? CurrentCapacity,
    bool Vital,
    NCBoneState? BoneState,
    float? BoneStability,
    NCSurgicalAccessDepth? AccessDepth,
    float RejectionBurden,
    NCTransplantRejectionStage? RejectionStage);

public readonly record struct NCAdminExactCyberwareSummary(
    Guid StableInstanceId,
    ProtoId<NCCyberwarePrototype> Profile,
    ProtoId<OrganCategoryPrototype>? HostCategory,
    ProtoId<NCCyberwareSlotPrototype>? Slot,
    float CurrentIntegrity,
    float MaximumIntegrity,
    float CurrentPower,
    float Heat,
    float Load,
    TimeSpan EmpDisabledUntil,
    float NeuralIntegrity,
    NCCyberwareNeuralState NeuralState,
    bool Calibrated,
    bool Operational,
    float FunctionalEfficiency);

public readonly record struct NCAdminExactDrugSummary(
    ProtoId<NCDrugProfilePrototype> Profile,
    NCDrugCategory Category,
    float Concentration,
    float CumulativeDose,
    TimeSpan LastAdministration);
