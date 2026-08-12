// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

public enum NCDrugCategory : byte
{
    Analgesic,
    Anesthetic,
    Sedative,
    Stimulant,
    Antibiotic,
    Procoagulant,
    Anticoagulant,
    Vasopressor,
    Vasodilator,
    Antiarrhythmic,
    RespiratoryStimulant,
    Antitoxin,
    Immunosuppressant,
    SyntheticOxygenCarrier,
    CyberwareService,
}

/// <summary>
/// Immutable aggregate consumed by physiology, wound progression and transplant systems. Detailed drug names
/// and concentrations remain server-only and are exposed only through capability-filtered diagnostics.
/// </summary>
public readonly record struct NCPharmacologyModifiers(
    float Analgesia,
    float Sedation,
    float Anesthesia,
    float Arousal,
    float CardiacOutputMultiplier,
    float RespiratoryTransferMultiplier,
    float OxygenationMultiplier,
    float OxygenCarrierContribution,
    float BleedingMultiplier,
    float RhythmStability,
    float RhythmInstability,
    float MotorControlMultiplier,
    float AntibioticPower,
    float Immunosuppression,
    float ToxinRecoveryPerSecond,
    float ShockContribution,
    float BrainFunctionMultiplier,
    float OverdoseFraction)
{
    public static readonly NCPharmacologyModifiers Neutral = new(
        0f,
        0f,
        0f,
        0f,
        1f,
        1f,
        1f,
        0f,
        1f,
        0f,
        0f,
        1f,
        0f,
        0f,
        0f,
        0f,
        1f,
        0f);
}

public readonly record struct NCDrugDoseResult(
    bool Accepted,
    ProtoId<NCDrugProfilePrototype> Profile,
    float PreviousConcentration,
    float CurrentConcentration,
    bool CapacityReached);

[ByRefEvent]
public readonly record struct NCPharmacologyChangedEvent(
    ProtoId<NCDrugProfilePrototype> Profile,
    float PreviousConcentration,
    float CurrentConcentration,
    float OverdoseBurden);
