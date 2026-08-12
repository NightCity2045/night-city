// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Persistence;

namespace Content.Server._NC.Medical.Persistence;

/// <summary>
/// Server-only binding between a materialized body and its stable persistence owner. Player bindings are
/// created solely from server preferences; named NPC bindings are created by the explicit A-Life API.
/// </summary>
[RegisterComponent]
public sealed partial class NCMedicalPersistenceOwnerComponent : Component
{
    [DataField]
    public NCMedicalStableOwnerKind Kind;

    [DataField]
    public string Key = string.Empty;

    [DataField]
    public long Revision;

    [DataField]
    public bool ExternalStorage;

    [DataField]
    public bool SaveBlocked;
}

/// <summary>
/// Declares who must persist a detached organ root. Detached organs are never folded into an unrelated
/// character row; an unassigned or external owner therefore fails closed until world/A-Life storage claims it.
/// </summary>
[RegisterComponent]
public sealed partial class NCDetachedOrganPersistenceOwnerComponent : Component
{
    [DataField]
    public NCMedicalStableOwnerKind Kind;

    [DataField]
    public string Key = string.Empty;

    [DataField]
    public bool RequiresExternalStorage = true;
}
