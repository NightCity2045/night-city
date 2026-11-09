// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Replicated mirror of "the controlled body has deliberately selected an anatomical zone". Only the boolean is
/// replicated so movement can be predicted; the selected zone itself stays private to the server.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), UnsavedComponent]
[Access(typeof(NCAimedMovementSystem))]
public sealed partial class NCAimedTargetingComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool ZoneSelected;
}

/// <summary>
/// Raised on an entity after its combat mode was toggled so movement-affecting systems can refresh.
/// </summary>
[ByRefEvent]
public readonly record struct NCCombatModeChangedEvent(bool InCombatMode);
