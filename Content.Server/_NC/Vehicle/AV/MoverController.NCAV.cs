// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Vehicle.AV.Systems;
using Content.Shared.Movement.Systems;

namespace Content.Server.Physics.Controllers;

public sealed partial class MoverController
{
    [Dependency] private NCAVSystem _ncAv = default!;

    protected override void HandleNCAVInput(EntityUid uid, NCAVButtons button, bool state)
    {
        _ncAv.SetPilotInput(uid, button, state);
    }
}
