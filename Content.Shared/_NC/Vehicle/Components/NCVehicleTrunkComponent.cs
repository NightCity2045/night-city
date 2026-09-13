// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Data-authored access rules for a vehicle's physical cargo compartment.</summary>
[RegisterComponent]
public sealed partial class NCVehicleTrunkComponent : Component
{
    /// <summary>Prevents passengers from operating the trunk through the vehicle entity.</summary>
    [DataField] public bool ExternalOnly = true;

    /// <summary>Living conscious mobs are excluded by default while bodies remain valid cargo.</summary>
    [DataField] public bool AllowLivingMobs;

    /// <summary>The lid cannot be operated while the vehicle is moving.</summary>
    [DataField] public float MaximumOpenSpeed = 0.1f;
}
