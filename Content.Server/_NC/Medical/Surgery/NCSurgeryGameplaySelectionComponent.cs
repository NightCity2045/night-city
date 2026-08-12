// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// One bounded, server-owned procedure intent for a surgeon. It is runtime interaction state only.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCSurgeryInteractionSystem), typeof(NCSurgeryConsoleSystem))]
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
    public EntityUid Facility;

    [DataField]
    public EntityUid? Graft;

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
/// Private anatomical planning state for one open console. It is deliberately separate from combat aim and
/// contains no client-selected patient entity; the console system fills patient and facility from live range.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCSurgeryConsoleSystem))]
public sealed partial class NCSurgeryConsolePlanningComponent : Component
{
    [DataField]
    public EntityUid Patient;

    [DataField]
    public EntityUid Facility;

    [DataField]
    public ProtoId<NCTargetZonePrototype> Zone;
}
