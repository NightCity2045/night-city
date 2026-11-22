// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>Identifies the temporary map owned by a moving grid.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, UnsavedComponent]
public sealed partial class NCZGridMotionMapComponent : Component
{
    [AutoNetworkedField]
    public EntityUid Grid;
}
