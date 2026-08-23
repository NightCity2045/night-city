// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>Data-only physical limits shared by every materialized human NPC.</summary>
[RegisterComponent]
public sealed partial class NCHumanSocialActionComponent : Component
{
    [DataField]
    public float ApproachRange = 1.5f;
}
