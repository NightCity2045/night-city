// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Data for a hand-held torque tool configured through the visual workshop.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleTorqueToolComponent : Component
{
    [DataField, AutoNetworkedField] public float Torque = 100;
    [DataField] public float Minimum = 20;
    [DataField] public float Maximum = 220;
    [DataField] public float Step = 5;
}
