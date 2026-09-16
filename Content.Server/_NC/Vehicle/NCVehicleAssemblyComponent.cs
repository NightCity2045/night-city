// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Assembly;

namespace Content.Server._NC.Vehicle;

/// <summary>Server-only round state. No persistence, global IDs, or implicit map serialization.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCVehicleAssemblyComponent : Component
{
    [ViewVariables] public Dictionary<string, NCVehicleInstalledPart> Installed = new();
    [ViewVariables] public NCVehicleAssemblyProfile Profile;
    [ViewVariables] public NCVehicleAssemblyProfile LoadedProfile;
    [ViewVariables] public TimeSpan NextMassRefresh;
    [ViewVariables] public NCVehicleAssemblyFailure Failure;
    [ViewVariables] public bool Initialized;
    [ViewVariables] public bool ProfileValid;
    [ViewVariables] public uint Revision;
    [ViewVariables] public Dictionary<string, uint> SlotRevisions = new();
    [ViewVariables] public Dictionary<string, EntityUid> ReservedSlots = new();
    [ViewVariables] public HashSet<string> DiagnosedSlots = new();
    [ViewVariables] public Dictionary<string, float> InspectedConditions = new();
    [ViewVariables] public HashSet<string> PreciselyDiagnosedSlots = new();
    [ViewVariables] public bool HoodOpen;
    [ViewVariables] public EntityUid? Lift;
}
