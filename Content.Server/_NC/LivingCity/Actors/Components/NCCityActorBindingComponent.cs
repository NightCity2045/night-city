// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameStates;

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>
/// Round-local marker binding a physical NPC entity to its persistent actor identity.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityActorBindingComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadOnly)]
    public NCActorId ActorId;
}
