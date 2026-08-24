// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition.Components;

/// <summary>Data-only round-local projection of the one action currently owned by a materialized human.</summary>
[RegisterComponent]
public sealed partial class NCHumanActionRuntimeComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid InstanceId;
    [ViewVariables(VVAccess.ReadOnly)] public NCActorId ActorId;
    [ViewVariables(VVAccess.ReadOnly)] public Guid IntentionId;
    [ViewVariables(VVAccess.ReadOnly)] public Guid PlanId;
    [ViewVariables(VVAccess.ReadOnly)] public string StepId = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public string ActionId = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCHumanActionExecutorKind Executor;
    [ViewVariables(VVAccess.ReadOnly)] public NCCognitionSubjectRef Target;
    [ViewVariables(VVAccess.ReadOnly)] public NCHumanActionRuntimeState State;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime StartedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime TimeoutAt;
    [ViewVariables(VVAccess.ReadOnly)] public string? Failure;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCHumanActionTraceEntry> Trace = [];
}
