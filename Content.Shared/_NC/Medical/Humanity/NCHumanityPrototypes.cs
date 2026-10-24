// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Humanity;

[Prototype("ncHumanityPackage")]
public sealed partial class NCHumanityPackagePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string AdaptationKey { get; private set; } = default!;
    [DataField] public bool PerAnatomicalContext { get; private set; } = true;
    [DataField] public float Burden { get; private set; }
    [DataField] public float AcuteCost { get; private set; }
    [DataField] public bool CrisisAffected { get; private set; }
}

[Prototype("ncHumanityProfile")]
public sealed partial class NCHumanityProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public TimeSpan ActiveUpdateInterval { get; private set; } = TimeSpan.FromSeconds(1);
    [DataField] public float SafeRecoveryPerSecond { get; private set; } = 0.1f;
    [DataField] public float OverloadPerSecond { get; private set; } = 0.15f;
    [DataField] public float OverburdenPerSecond { get; private set; } = 0.01f;
    [DataField] public float VulnerabilityScale { get; private set; } = 2f;
    [DataField] public float StableMinimumPerfusion { get; private set; } = 0.7f;
    [DataField] public float StableMinimumOxygenation { get; private set; } = 0.8f;
    [DataField] public float StableMaximumShock { get; private set; } = 0.2f;
    [DataField] public float NeuralTraumaThreshold { get; private set; } = 0.35f;
    [DataField] public float StressHysteresis { get; private set; } = 5f;
    [DataField] public float HumanityHysteresis { get; private set; } = 2f;
    [DataField] public float StabilizationSeconds { get; private set; } = 30f;
    [DataField] public List<NCHumanityCrisisBand> Bands { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCHumanityCrisisBand
{
    [DataField(required: true)] public NCCyberCrisisStage Stage { get; private set; }
    [DataField] public float HumanityBelow { get; private set; }
    [DataField] public float StressAtLeast { get; private set; }
    [DataField] public float EnhancementEfficiency { get; private set; } = 1f;
    [DataField] public bool BlocksEnhancedModes { get; private set; }
}

[Prototype("ncHumanityStimulus")]
public sealed partial class NCHumanityStimulusPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public float Impulse { get; private set; }
    [DataField] public float CooldownSeconds { get; private set; } = 30f;
}
