// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>
/// Round-local ownership data for controllers that need vanilla NPC steering without necessarily owning an HTN.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityNpcSteeringLeaseComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public HashSet<string> Owners = [];
    [ViewVariables(VVAccess.ReadOnly)] public bool AddedActiveNpc;
}
