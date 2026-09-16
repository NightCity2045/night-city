// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Input;

namespace Content.Shared._NC.Vehicle;

[KeyFunctions]
public static class NCVehicleKeyFunctions
{
    public static readonly BoundKeyFunction Throttle = "NCVehicleThrottle";
    public static readonly BoundKeyFunction Brake = "NCVehicleBrake";
    public static readonly BoundKeyFunction Clutch = "NCVehicleClutch";
    public static readonly BoundKeyFunction Handbrake = "NCVehicleHandbrake";
    public static readonly BoundKeyFunction GearUp = "NCVehicleGearUp";
    public static readonly BoundKeyFunction GearDown = "NCVehicleGearDown";
    public static readonly BoundKeyFunction GearLeft = "NCVehicleGearLeft";
    public static readonly BoundKeyFunction GearRight = "NCVehicleGearRight";
    public static readonly BoundKeyFunction SteerLeft = "NCVehicleSteerLeft";
    public static readonly BoundKeyFunction SteerRight = "NCVehicleSteerRight";
}
