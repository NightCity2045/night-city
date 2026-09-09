// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Round-local seat link on the physical person, independent of player session attachment.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleOccupantComponent : Component
{
    [AutoNetworkedField] public EntityUid Vehicle;
    [AutoNetworkedField] public string Seat = string.Empty;
    [AutoNetworkedField] public bool Belted;
}
