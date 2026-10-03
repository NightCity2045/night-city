// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>
/// Moves a body directly to an adjacent Z-level when it enters this stair tile.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCZStairTransitionComponent : Component
{
    /// <summary>
    /// Relative Z-level destination. Positive values move up; negative values move down.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int LevelOffset;
}
