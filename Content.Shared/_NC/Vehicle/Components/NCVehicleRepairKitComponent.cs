// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehicleRepairKitComponent : Component
{
    [DataField] public string? Mount;
    [DataField] public string Category = "Mechanical";
    [DataField] public float RestoreAmount = 0.5f;
    [DataField] public float MaximumCondition = 0.85f;
    [DataField] public float Seconds = 60;
    [DataField] public float WorkbenchRange = 3;
    [DataField, AutoNetworkedField] public int Uses = 1;
}
