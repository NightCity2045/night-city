// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>Prevents one mechanic from scheduling overlapping operations on a component machine.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCVehicleWorkbenchSessionComponent : Component
{
    [ViewVariables] public EntityUid? Bench;
    [ViewVariables] public bool Pending;
}
