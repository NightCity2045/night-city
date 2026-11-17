// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>
/// Legacy stair marker retained for loading existing maps. Movement now uses NCZLevelHighGround.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCZStairTransitionComponent : Component
{
    /// <summary>
    /// Former Z-level destination, retained for map deserialization.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int LevelOffset;
}
