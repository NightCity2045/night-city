// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Public vehicle identity within the round. Private assembly state is not broadcast to observers.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public ProtoId<NCVehicleChassisPrototype> Chassis;
    [DataField] public float InfrastructureSearchRange = 6;
}
