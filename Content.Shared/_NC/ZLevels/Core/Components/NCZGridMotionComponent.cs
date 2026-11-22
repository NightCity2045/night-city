// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>One timed move of a grid between adjacent, existing Z-level maps.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause, UnsavedComponent]
public sealed partial class NCZGridMotionComponent : Component
{
    [AutoNetworkedField]
    public EntityUid SourceMap;

    [AutoNetworkedField]
    public EntityUid TargetMap;

    [AutoNetworkedField]
    public int Direction;

    [AutoNetworkedField, AutoPausedField]
    public TimeSpan StartedAt;

    [AutoNetworkedField, AutoPausedField]
    public TimeSpan ArrivesAt;
}
