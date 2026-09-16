// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Physical workshop capabilities; no autonomous workers or background repair.</summary>
[RegisterComponent]
public sealed partial class NCVehicleWorkshopEquipmentComponent : Component
{
    [DataField] public bool Lift;
    [DataField] public bool Hoist;
    [DataField] public bool Workbench;
    /// <summary>Allows a carrier and its extracted children to be separated or assembled in the equipment slot.</summary>
    [DataField] public bool Assembly;
    [DataField] public float Range = 3;
}
