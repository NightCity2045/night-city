// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Personnel.Components;

/// <summary>
/// Data-only configuration for a reusable organization personnel console.
/// Authorization and per-user selections remain server-owned.
/// </summary>
[RegisterComponent]
public sealed partial class NCPersonnelConsoleComponent : Component
{
    [DataField(required: true)]
    public ProtoId<DepartmentPrototype> Department;

    [DataField]
    public int CandidateLimit = 100;

    [DataField]
    public int HistoryLimit = 100;

    [DataField]
    public uint MaximumSearchLength = 64;

    [DataField]
    public uint MaximumReasonLength = 512;
}
