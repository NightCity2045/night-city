// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>Round-local children of a physical removed carrier. Private measurements are not replicated.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCVehicleDetachedAssemblyComponent : Component
{
    [ViewVariables] public ProtoId<NCVehicleChassisPrototype> Chassis;
    [ViewVariables] public string RootSlot = string.Empty;
    [ViewVariables] public Dictionary<string, NCVehicleInstalledPart> Parts = new();
    /// <summary>Rejects stale visual requests after any bench-side structural change.</summary>
    [ViewVariables] public uint Revision;
}
