// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Vehicle;

/// <summary>Fluid circuits exposed by authored workshop service points.</summary>
[Serializable, NetSerializable]
public enum NCVehicleFluidCircuit : byte
{
    Oil,
    Coolant,
    Brakes,
    Transmission,
    Steering,
    Washer,
    Fuel,
}
