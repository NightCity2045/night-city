// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>Private per-player observations and pending action; never attached to the public vehicle state.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCVehicleWorkshopSessionComponent : Component
{
    [ViewVariables] public EntityUid? Vehicle;
    [ViewVariables] public uint Sequence;
    [ViewVariables] public bool Pending;
    [ViewVariables] public LocId Feedback = "nc-workshop-ready";
    [ViewVariables] public Dictionary<string, NCVehicleWorkshopMeasurement> Measurements = new();
    [ViewVariables] public TimeSpan NextRefresh;
}

public readonly record struct NCVehicleWorkshopMeasurement(float Condition, bool Exact, uint Revision);
