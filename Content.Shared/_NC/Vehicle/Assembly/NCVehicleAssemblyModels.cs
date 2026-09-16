// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Assembly;

/// <summary>Immutable in-memory part state. Condition is normalized; damage does not remove mass.</summary>
public readonly record struct NCVehicleInstalledPart(ProtoId<NCVehiclePartPrototype> Prototype, float Condition, bool Connected = true, bool Fastened = true, float? StoredCharge = null,
    Dictionary<string, bool>? WorkPoints = null, Dictionary<string, float>? PointTorque = null);

/// <summary>Derived assembly data, not a second physics body or an authorization to drive.</summary>
public readonly record struct NCVehicleAssemblyProfile(
    float Mass,
    Vector2 CenterOfMass,
    float Inertia,
    bool RequiredSlotsFilled);

/// <summary>Internal validation result; a future UI must localize it rather than display enum names.</summary>
public enum NCVehicleAssemblyError : byte
{
    None,
    InvalidChassis,
    InvalidSlot,
    MissingDependency,
    DependencyCycle,
    UnknownSlot,
    UnknownPart,
    InvalidPart,
    IncompatibleMount,
    UnoccupiedDependency,
    InvalidCondition,
    InvalidMassProperties,
}

public readonly record struct NCVehicleAssemblyFailure(NCVehicleAssemblyError Error, string Slot = "");
