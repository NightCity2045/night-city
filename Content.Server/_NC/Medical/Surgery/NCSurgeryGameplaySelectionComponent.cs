// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.ActionResolution;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// One bounded, server-owned procedure intent for a surgeon. It is runtime interaction state only.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCSurgeryInteractionSystem))]
public sealed partial class NCSurgeryGameplaySelectionComponent : Component
{
    [DataField]
    public EntityUid Patient;

    [DataField]
    public ProtoId<NCSurgeryProcedurePrototype> Procedure;

    /// <summary>
    /// Exact live region selected by the server from the current target-doll branch. The client sends only
    /// stable prototype identities; every interaction revalidates that this region still belongs to Patient.
    /// </summary>
    [DataField]
    public EntityUid TargetRegion;

    [DataField]
    public EntityUid? Facility;

    [DataField]
    public EntityUid? Graft;

    /// <summary>
    /// Direct interactions may skip only leading steps explicitly marked optional by their prototypes.
    /// </summary>
    [DataField]
    public int InitialStepIndex;

    [ViewVariables]
    public bool StepInProgress;

    [ViewVariables]
    public int ExecutingStepIndex = -1;

    [ViewVariables]
    public TimeSpan StepStartedAt;

    [ViewVariables]
    public TimeSpan StepEndsAt;
}

/// <summary>
/// Short-lived server-owned ambiguity resolution for one physical tool interaction. Candidate entities never
/// leave the server; the client may only echo a prototype/category pair from the offered bounded set.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCSurgeryInteractionSystem))]
public sealed partial class NCSurgeryDirectChoiceComponent : Component
{
    [ViewVariables]
    public NCActionResolutionId OfferId;

    [ViewVariables]
    public EntityUid Patient;

    [ViewVariables]
    public EntityUid Tool;

    [ViewVariables]
    public EntityUid? Facility;

    [ViewVariables]
    public TimeSpan ExpiresAt;

    public readonly List<NCSurgeryDirectCandidate> Candidates = [];
}

public readonly record struct NCSurgeryDirectCandidate(
    ProtoId<NCSurgeryProcedurePrototype> Procedure,
    ProtoId<OrganCategoryPrototype> TargetCategory,
    EntityUid TargetRegion,
    EntityUid? Graft,
    int InitialStepIndex);
