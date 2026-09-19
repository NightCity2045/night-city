// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Vehicle.AV.Systems;
using Content.Shared.Movement.Systems;

namespace Content.Server._NC.Vehicle.AV.Components;

/// <summary>Server-side vertical input associated with a shuttle pilot.</summary>
[RegisterComponent, Access(typeof(NCAVSystem))]
public sealed partial class NCAVPilotComponent : Component
{
    [ViewVariables]
    public EntityUid? AV;

    [ViewVariables]
    public NCAVButtons HeldButtons;
}
