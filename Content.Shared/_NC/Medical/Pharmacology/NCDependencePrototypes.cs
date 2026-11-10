// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Tolerance, physical dependence, craving and withdrawal of one class of compound. Everything is data: a
/// single standard dose must never be enough to create a dependence, and withdrawal is a managed stage
/// sequence that never takes control of the character.
/// </summary>
[Prototype("ncDependenceProfile")]
public sealed partial class NCDependenceProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Tolerance gained per unit of concentration actually dosed.</summary>
    [DataField]
    public float ToleranceGainPerConcentration { get; private set; } = 0.01f;

    /// <summary>Highest share of the therapeutic effect that tolerance can remove.</summary>
    [DataField]
    public float MaximumTolerance { get; private set; } = 0.6f;

    [DataField]
    public float ToleranceDecayPerSecond { get; private set; } = 0.0004f;

    [DataField]
    public float DependenceGainPerConcentration { get; private set; } = 0.004f;

    [DataField]
    public float DependenceDecayPerSecond { get; private set; } = 0.0001f;

    /// <summary>Physical dependence at which stopping the compound starts to cause symptoms.</summary>
    [DataField]
    public float WithdrawalThreshold { get; private set; } = 0.25f;

    /// <summary>How long after the last dose the body waits before withdrawal can begin.</summary>
    [DataField]
    public TimeSpan WithdrawalOnset { get; private set; } = TimeSpan.FromMinutes(2);

    /// <summary>Time over which withdrawal grows from craving to its full stage.</summary>
    [DataField]
    public TimeSpan WithdrawalRamp { get; private set; } = TimeSpan.FromMinutes(6);

    /// <summary>Symptom compound carried by withdrawal. It uses the ordinary drug physiology.</summary>
    [DataField]
    public ProtoId<NCDrugProfilePrototype>? WithdrawalCompound { get; private set; }

    /// <summary>Concentration of the symptom compound at full dependence and full withdrawal.</summary>
    [DataField]
    public float WithdrawalConcentrationAtFull { get; private set; } = 4f;

    [DataField]
    public float CravingGainPerSecond { get; private set; } = 0.004f;

    [DataField]
    public float CravingDecayPerSecond { get; private set; } = 0.01f;

    /// <summary>Compounds that stand in for this one: while one is active, withdrawal is suppressed.</summary>
    [DataField]
    public List<ProtoId<NCDrugProfilePrototype>> ReplacementDrugs { get; private set; } = [];

    /// <summary>How much faster dependence fades while a replacement is active (controlled tapering).</summary>
    [DataField]
    public float ReplacementDecayMultiplier { get; private set; } = 4f;
}
