// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Physiology;

/// <summary>
/// All authoritative human circulation, consciousness and scheduler thresholds.
/// Keeping them in prototypes allows balance changes without recompiling medical logic.
/// </summary>
[Prototype("ncPhysiologyProfile")]
public sealed partial class NCPhysiologyProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public float NominalBloodVolume { get; private set; }

    /// <summary>
    /// Persistence-safe default recipient token used by data-driven emergency transfusion compatibility.
    /// </summary>
    [DataField(required: true)]
    public string DefaultTransfusionCompatibility { get; private set; } = string.Empty;

    [DataField(required: true)]
    public float CriticalPerfusionThreshold { get; private set; }

    [DataField(required: true)]
    public float ArrestPerfusionThreshold { get; private set; }

    [DataField(required: true)]
    public float BrainInjuryPerfusionThreshold { get; private set; }

    [DataField(required: true)]
    public TimeSpan BrainDeathDelay { get; private set; }

    /// <summary>
    /// Maximum time spent in clinical death during which circulation may still be restored.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan RevivableDeathDuration { get; private set; }

    [DataField]
    public float BrainRecoveryRate { get; private set; } = 0.25f;

    [DataField(required: true)]
    public float DazedThreshold { get; private set; }

    [DataField(required: true)]
    public float ConfusedThreshold { get; private set; }

    [DataField(required: true)]
    public float StuporousThreshold { get; private set; }

    [DataField(required: true)]
    public float UnconsciousThreshold { get; private set; }

    [DataField(required: true)]
    public float ComatoseThreshold { get; private set; }

    /// <summary>
    /// Inclusive lower bounds for Mild through Overwhelming pain.
    /// </summary>
    [DataField(required: true)]
    public List<float> PainThresholds { get; private set; } = [];

    /// <summary>
    /// Inclusive lower bounds for Compensated through Refractory shock.
    /// </summary>
    [DataField(required: true)]
    public List<float> ShockThresholds { get; private set; } = [];

    [DataField]
    public float PainShockContribution { get; private set; } = 0.35f;

    [DataField]
    public float ShockConsciousnessPenalty { get; private set; } = 0.15f;

    [DataField]
    public TimeSpan CriticalUpdateInterval { get; private set; } = TimeSpan.FromSeconds(0.25);

    [DataField]
    public TimeSpan ActiveBleedingUpdateInterval { get; private set; } = TimeSpan.FromSeconds(0.5);

    [DataField]
    public TimeSpan StableInjuredUpdateInterval { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Hard per-tick cap for scheduled body evaluations. Excess work remains queued with its original timestamp.
    /// </summary>
    [DataField]
    public int MaximumScheduledBodiesPerTick { get; private set; } = 4;

    /// <summary>
    /// Wall-clock budget for scheduled physiology work performed during one tick.
    /// </summary>
    [DataField]
    public TimeSpan MaximumSchedulerWorkTime { get; private set; } = TimeSpan.FromMilliseconds(3);

    /// <summary>
    /// Stable delay window that spreads recurring evaluations across ticks.
    /// </summary>
    [DataField]
    public TimeSpan SchedulingPhaseWindow { get; private set; } = TimeSpan.FromMilliseconds(100);

    [DataField]
    public TimeSpan CavityDrainUpdateInterval { get; private set; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Consequences of pressure and organ compression in each cavity. Values are evaluated from the live
    /// cavity components, keeping physiology balance in YAML rather than hardcoded by cavity kind.
    /// </summary>
    [DataField]
    public Dictionary<NCBodyCavity, NCCavityPhysiologyDefinition> CavityPhysiology { get; private set; } = new();
}

[DataDefinition]
public sealed partial class NCCavityPhysiologyDefinition
{
    [DataField]
    public float CardiacOutputPenaltyAtFullCompression;

    [DataField]
    public float RespiratoryTransferPenaltyAtFullCompression;

    [DataField]
    public float BrainFunctionPenaltyAtFullCompression;

    [DataField]
    public float PainAtFullPressure;

    [DataField]
    public float ShockAtFullPressure;
}
