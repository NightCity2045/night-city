// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Physical diagnostic instrument. Precision and supported service categories are authored in content.</summary>
[RegisterComponent]
public sealed partial class NCVehicleDiagnosticToolComponent : Component
{
    [DataField] public int Level = 1;
    [DataField] public HashSet<string> Categories = new();
}
