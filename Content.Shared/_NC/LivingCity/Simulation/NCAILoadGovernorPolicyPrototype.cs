// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Simulation;

/// <summary>
/// Data-driven thresholds and degradation profiles for the physical GPT-NPC load governor.
/// The governor changes update cadence and queue throughput; it never removes NPC bodies or rejects spawns.
/// </summary>
[Prototype("ncAILoadGovernorPolicy")]
public sealed partial class NCAILoadGovernorPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public TimeSpan SampleInterval { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public float FrameTimeEwmaAlpha { get; private set; } = 0.15f;

    [DataField]
    public int DegradeSamples { get; private set; } = 3;

    [DataField]
    public int RecoverSamples { get; private set; } = 5;

    [DataField]
    public float ElevatedEnterFrameMilliseconds { get; private set; } = 28f;

    [DataField]
    public float ElevatedExitFrameMilliseconds { get; private set; } = 22f;

    [DataField]
    public float CriticalEnterFrameMilliseconds { get; private set; } = 45f;

    [DataField]
    public float CriticalExitFrameMilliseconds { get; private set; } = 32f;

    [DataField]
    public float ElevatedEnterWorkRatio { get; private set; } = 0.12f;

    [DataField]
    public float ElevatedExitWorkRatio { get; private set; } = 0.08f;

    [DataField]
    public float CriticalEnterWorkRatio { get; private set; } = 0.18f;

    [DataField]
    public float CriticalExitWorkRatio { get; private set; } = 0.13f;

    [DataField]
    public float ElevatedEnterQueueRatio { get; private set; } = 0.60f;

    [DataField]
    public float ElevatedExitQueueRatio { get; private set; } = 0.35f;

    [DataField]
    public float CriticalEnterQueueRatio { get; private set; } = 0.85f;

    [DataField]
    public float CriticalExitQueueRatio { get; private set; } = 0.60f;

    [DataField]
    public int ElevatedBacklogCount { get; private set; } = 2;

    [DataField]
    public int CriticalBacklogCount { get; private set; } = 3;

    [DataField]
    public int CriticalExitBacklogCount { get; private set; } = 2;

    [DataField]
    public float ElevatedEvaluationIntervalMultiplier { get; private set; } = 2f;

    [DataField]
    public float CriticalEvaluationIntervalMultiplier { get; private set; } = 4f;

    [DataField]
    public float ElevatedDispatchMultiplier { get; private set; } = 0.5f;

    [DataField]
    public float CriticalDispatchMultiplier { get; private set; } = 0.25f;

}
