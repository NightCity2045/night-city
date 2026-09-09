// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Physical recovery platform. Its occupant is the same car entity, never a saved replacement.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleRecoveryComponent : Component
{
    [AutoNetworkedField] public EntityUid? Cargo;
    [AutoNetworkedField] public bool Secured;
    [AutoNetworkedField] public bool Winching;
    [DataField] public string Container = "nc-vehicle-flatbed";
    [DataField] public Vector2 DeckPosition = new(0, -1);
    [DataField] public Vector2 UnloadPosition = new(0, -6.5f);
    [DataField] public Vector2 CargoHalfExtents = new(1, 2.2f);
    [DataField] public float MaximumLoad = 2400;
    [DataField] public float OperationRange = 5;
    [DataField] public float MaximumSpeed = 0.15f;
    [DataField] public float WinchRate = 0.3f;
    [DataField] public float LoadGap = 1;
    [DataField] public float MinimumAlignmentDot = 0.95f;
    [DataField] public float WinchAmps = 100;
    [DataField] public float LoadingSeconds = 20;
    [DataField] public float SecuringSeconds = 15;
    [ViewVariables] public EntityUid? Operator;
}
