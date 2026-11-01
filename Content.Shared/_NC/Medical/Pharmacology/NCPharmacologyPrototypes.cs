// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Systemic;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Whole-human pharmacokinetic limits and overdose consequences. All rates are content values so medical
/// balance can be tuned without changing the deterministic resolver.
/// </summary>
[Prototype("ncPharmacologyProfile")]
public sealed partial class NCPharmacologyProfilePrototype : IPrototype
{
    [DataField] public float MaximumHumanityRecoveryPerSecond { get; private set; } = 2f / 60f;
    [DataField] public float MaximumCrisisSuppressionPerSecond { get; private set; } = 2f;
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public int ActiveDrugCapacity { get; private set; }

    [DataField(required: true)]
    public TimeSpan ActiveUpdateInterval { get; private set; }

    [DataField(required: true)]
    public float MaximumOverdoseBurden { get; private set; }

    [DataField(required: true)]
    public float OverdoseRecoveryPerSecond { get; private set; }

    [DataField(required: true)]
    public float MaximumRecordedCumulativeDose { get; private set; }

    [DataField(required: true)]
    public TimeSpan MaximumRecordedExposureAge { get; private set; }

    [DataField(required: true)]
    public float MinimumPhysiologyMultiplier { get; private set; }

    [DataField(required: true)]
    public float MaximumPhysiologyMultiplier { get; private set; }

    [DataField(required: true)]
    public float MinimumClottingMultiplier { get; private set; }

    [DataField(required: true)]
    public float MaximumClottingMultiplier { get; private set; }

    [DataField(required: true)]
    public float MaximumInteractionMultiplier { get; private set; }

    [DataField(required: true)]
    public float MaximumToxinRecoveryPerSecond { get; private set; }

    [DataField(required: true)]
    public float OverdoseShockAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OverdoseBrainPenaltyAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OverdoseCardiacPenaltyAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OverdoseRespiratoryPenaltyAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OverdoseRhythmInstabilityAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OverdoseArrhythmiaThreshold { get; private set; }

    [DataField(required: true)]
    public float OverdoseMotorControlPenaltyAtMaximum { get; private set; }

    [DataField(required: true)]
    public float OrganDamageThreshold { get; private set; }

    [DataField(required: true)]
    public float LiverDamagePerSecondAtMaximum { get; private set; }

    [DataField(required: true)]
    public float KidneyDamagePerSecondAtMaximum { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCSystemicDamageProfilePrototype> ToxinSystemicProfile { get; private set; }
}

/// <summary>
/// Pairwise deterministic interaction. Both profiles must declare the same rule; semantic tests enforce the
/// symmetry so dictionary iteration order cannot affect the result.
/// </summary>
[DataDefinition]
public sealed partial class NCDrugInteractionRule
{
    [DataField]
    public float TherapeuticMultiplier { get; private set; } = 1f;

    [DataField]
    public float ToxicityMultiplier { get; private set; } = 1f;
}

/// <summary>
/// Pharmacodynamic and clearance profile for one reagent effect. Values at therapeutic concentration are
/// linearly scaled by measured concentration and clamped; overdose is a separate content-defined burden.
/// </summary>
[Prototype("ncDrugProfile")]
public sealed partial class NCDrugProfilePrototype : IPrototype
{
    [DataField] public float HumanityRecoveryPerSecondAtTherapeutic { get; private set; }
    [DataField] public float CrisisSuppressionPerSecondAtTherapeutic { get; private set; }
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCDrugCategory Category { get; private set; }

    [DataField(required: true)]
    public float MaximumConcentration { get; private set; }

    [DataField(required: true)]
    public float RemovalConcentration { get; private set; }

    [DataField(required: true)]
    public float MinimumEffectiveConcentration { get; private set; }

    [DataField(required: true)]
    public float TherapeuticConcentration { get; private set; }

    [DataField(required: true)]
    public float ToxicConcentration { get; private set; }

    [DataField(required: true)]
    public float ClearancePerSecond { get; private set; }

    [DataField]
    public float BaselineClearanceFraction { get; private set; }

    [DataField]
    public float LiverClearanceFraction { get; private set; }

    [DataField]
    public float KidneyClearanceFraction { get; private set; }

    [DataField]
    public float AnalgesiaAtTherapeutic { get; private set; }

    [DataField]
    public float SedationAtTherapeutic { get; private set; }

    [DataField]
    public float AnesthesiaAtTherapeutic { get; private set; }

    [DataField]
    public float ArousalAtTherapeutic { get; private set; }

    [DataField]
    public float CardiacOutputMultiplierAtTherapeutic { get; private set; } = 1f;

    [DataField]
    public float RespiratoryTransferMultiplierAtTherapeutic { get; private set; } = 1f;

    [DataField]
    public float OxygenationMultiplierAtTherapeutic { get; private set; } = 1f;

    [DataField]
    public float OxygenCarrierContributionAtTherapeutic { get; private set; }

    [DataField]
    public float BleedingMultiplierAtTherapeutic { get; private set; } = 1f;

    [DataField]
    public float RhythmStabilityAtTherapeutic { get; private set; }

    [DataField]
    public float RhythmInstabilityAtTherapeutic { get; private set; }

    [DataField]
    public float MotorControlMultiplierAtTherapeutic { get; private set; } = 1f;

    [DataField]
    public float AntibioticPowerAtTherapeutic { get; private set; }

    [DataField]
    public float ImmunosuppressionAtTherapeutic { get; private set; }

    [DataField]
    public float ToxinRecoveryPerSecondAtTherapeutic { get; private set; }

    [DataField]
    public float OverdoseBurdenPerSecondAtMaximum { get; private set; }

    [DataField]
    public Dictionary<ProtoId<NCDrugProfilePrototype>, NCDrugInteractionRule> Interactions { get; private set; } = new();
}
