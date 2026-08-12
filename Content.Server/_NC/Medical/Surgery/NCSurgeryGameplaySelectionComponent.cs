// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Surgery;
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
}
