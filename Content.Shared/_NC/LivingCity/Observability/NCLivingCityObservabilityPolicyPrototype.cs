// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Observability;

/// <summary>
/// Data-driven operational alert thresholds. They diagnose runtime health and never alter gameplay outcomes.
/// </summary>
[Prototype("ncLivingCityObservabilityPolicy")]
public sealed partial class NCLivingCityObservabilityPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public TimeSpan EvaluationInterval { get; private set; } = TimeSpan.FromSeconds(5);
    [DataField] public float FrameP95WarningMilliseconds { get; private set; } = 28f;
    [DataField] public float FrameP95CriticalMilliseconds { get; private set; } = 45f;
    [DataField] public float PersistenceP95WarningMilliseconds { get; private set; } = 75f;
    [DataField] public float PersistenceP95CriticalMilliseconds { get; private set; } = 250f;
    [DataField] public float AllocatedMegabytesWarning { get; private set; } = 96f;
    [DataField] public float AllocatedMegabytesCritical { get; private set; } = 256f;
    [DataField] public int NeuralFailuresWarning { get; private set; } = 2;
    [DataField] public int NeuralFailuresCritical { get; private set; } = 5;
    [DataField] public int OutboxLagWarningSeconds { get; private set; } = 30;
    [DataField] public int OutboxLagCriticalSeconds { get; private set; } = 120;
    [DataField] public int MaximumRetainedAlerts { get; private set; } = 32;
}
