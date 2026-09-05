// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Neural;

/// <summary>Data-driven cadence for independent GPT decisions; it never limits physical NPC count.</summary>
[Prototype("ncPhysicalActorBrainPolicy")]
public sealed partial class NCPhysicalActorBrainPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public TimeSpan DecisionInterval { get; private set; }
    [DataField(required: true)] public TimeSpan RetryInterval { get; private set; }
    [DataField(required: true)] public TimeSpan InitialStagger { get; private set; }
    [DataField(required: true)] public int MaximumDispatchesPerTick { get; private set; }
    [DataField(required: true)] public int MaximumExecutionPulsesPerTick { get; private set; }
    [DataField(required: true)] public TimeSpan ExecutionPulseInterval { get; private set; }
    [DataField(required: true)] public TimeSpan ActionPlanLifetime { get; private set; }
}
