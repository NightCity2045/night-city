// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Vehicle.AV.Systems;

namespace Content.Server._NC.Vehicle.AV.Components;

/// <summary>Prototype-configured lift supplied by one anchored, powered AV lift fan.</summary>
[RegisterComponent, Access(typeof(NCAVSystem))]
public sealed partial class NCAVLifterComponent : Component
{
    [DataField(required: true)]
    public float Lift;
}
