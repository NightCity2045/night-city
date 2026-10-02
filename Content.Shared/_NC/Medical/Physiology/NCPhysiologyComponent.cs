// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Physiology;

/// <summary>
/// Server-authoritative whole-body result derived from local wounds, circulation and organ function.
/// No field is backed by Damageable or a global HP threshold.
/// </summary>
[RegisterComponent]
public sealed partial class NCPhysiologyComponent : Component
{
    [DataField]
    public float Oxygenation = 1f;

    [DataField]
    public float TotalPain;

    [DataField]
    public NCPainLevel PainLevel;

    [DataField]
    public float Shock;

    [DataField]
    public NCShockStage ShockStage;

    [DataField]
    public NCConsciousnessState Consciousness = NCConsciousnessState.Alert;

    [DataField]
    public NCPhysiologicalCondition Condition = NCPhysiologicalCondition.Stable;

    [DataField]
    public float BrainFunction = 1f;

    [DataField]
    public TimeSpan TimeBelowCriticalPerfusion;

    /// <summary>
    /// Continuous time without spontaneous circulation. It resets when the body leaves clinical arrest.
    /// </summary>
    [DataField]
    public TimeSpan TimeInClinicalDeath;

    [DataField]
    public bool Irreversible;
}

public enum NCPainLevel : byte
{
    None,
    Mild,
    Moderate,
    Severe,
    Excruciating,
    Overwhelming,
}

public enum NCShockStage : byte
{
    None,
    Compensated,
    Moderate,
    Severe,
    Refractory,
}

public enum NCConsciousnessState : byte
{
    Alert,
    Dazed,
    Confused,
    Stuporous,
    Unconscious,
    Comatose,
    BrainDead,
}

public enum NCPhysiologicalCondition : byte
{
    Stable,
    Compensating,
    Critical,
    ClinicalArrest,
    Dead,
}
