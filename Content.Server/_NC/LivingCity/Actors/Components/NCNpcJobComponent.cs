// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>
/// Server-authoritative job context for a physical GPT NPC. It grants no job slot by itself.
/// </summary>
[RegisterComponent]
public sealed partial class NCNpcJobComponent : Component
{
    [DataField(required: true)]
    public ProtoId<JobPrototype> Job;

    /// <summary>
    /// Test actors may carry vanilla-issued items that do not have persistent economy identities.
    /// Such items are reissued next round instead of being projected into durable inventory.
    /// </summary>
    [DataField]
    public bool IgnoreUntrackedInventory;
}
