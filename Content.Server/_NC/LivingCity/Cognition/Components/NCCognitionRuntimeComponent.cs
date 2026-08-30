// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition.Components;

/// <summary>
/// Round-local inspection adapter for a materialized actor. Decision logic is owned by NCCognitionSystem.
/// </summary>
[RegisterComponent]
public sealed partial class NCCognitionRuntimeComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCActorId ActorId;
    [ViewVariables(VVAccess.ReadOnly)] public long ActorVersion;
    [ViewVariables(VVAccess.ReadOnly)] public long CognitionVersion;
    [ViewVariables(VVAccess.ReadOnly)] public long BeliefSetVersion;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime LastDecisionAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime NextDecisionAt;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCognitionFact> Facts = [];
    [ViewVariables(VVAccess.ReadOnly)] public List<NCBeliefSnapshot> Beliefs = [];
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCognitionDesireSnapshot> Desires = [];
    [ViewVariables(VVAccess.ReadOnly)] public NCCognitionIntentionSnapshot? Intention;
    [ViewVariables(VVAccess.ReadOnly)] public NCCognitionPlanSnapshot? Plan;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCognitionTraceEntry> Trace = [];
}

