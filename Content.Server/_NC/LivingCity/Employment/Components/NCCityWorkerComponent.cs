// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment.Components;

/// <summary>
/// Server-only physical projection of canonical persistent employment. It carries no authority and disappears with
/// the materialized NPC entity.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityWorkerComponent : Component
{
    [DataField]
    public ProtoId<DepartmentPrototype> Organization;

    [DataField]
    public ProtoId<JobPrototype> Job;
}
