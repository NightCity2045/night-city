// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._NC.ZLevels.Core.Components;

/// <summary>
/// Marks an entity that has fallen below the last available Z-level.
/// The Z-level system owns its deletion; no physical chasm entity exists there.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class NCZLevelTerminalFallComponent : Component
{
    /// <summary>
    /// Active game time at which the terminal fall animation is complete.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan DeleteAt;
}
