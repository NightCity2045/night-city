// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Mapper-authored physical passage between live maps. Duplicate destination IDs fail closed.</summary>
[RegisterComponent]
public sealed partial class NCVehicleGateComponent : Component
{
    [DataField(required: true)] public string GateId = string.Empty;
    [DataField(required: true)] public string Destination = string.Empty;
    [DataField] public Vector2 ArrivalOffset;
    [DataField] public float Radius = 5;
    [DataField] public float MaximumSpeed = 0.2f;
    [DataField] public Vector2 MaximumHalfExtents = new(1.5f, 4.5f);
}

/// <summary>A parking reservation marks a physical bay; the car stays on its map and can still be stolen.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleParkingComponent : Component
{
    [AutoNetworkedField] public EntityUid? Owner;
    [AutoNetworkedField] public EntityUid? Vehicle;
    [DataField] public float Radius = 3;
    [DataField] public float MaximumSpeed = 0.1f;
}
