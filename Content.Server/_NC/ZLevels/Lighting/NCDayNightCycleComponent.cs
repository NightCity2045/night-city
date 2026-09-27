// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Maths;

namespace Content.Server._NC.ZLevels.Lighting;

/// <summary>
/// Data-driven configuration for a synchronized day/night cycle across an NC Z-level network.
/// </summary>
[RegisterComponent]
public sealed partial class NCDayNightCycleComponent : Component
{
    /// <summary>
    /// Lowest Z-level depth that receives outdoor lighting. Lower levels retain static map light.
    /// </summary>
    [DataField(required: true)]
    public int MinimumDepth;

    /// <summary>
    /// In-world hour represented when the round clock is zero.
    /// </summary>
    [DataField(required: true)]
    public float RoundStartHour;

    [DataField(required: true)]
    public TimeSpan Duration;

    [DataField(required: true)]
    public float MinLightLevel;

    [DataField(required: true)]
    public float MaxLightLevel;

    [DataField(required: true)]
    public float ClipLight;

    [DataField(required: true)]
    public Color ClipLevel;

    [DataField(required: true)]
    public Color MinLevel;

    [DataField(required: true)]
    public Color MaxLevel;
}
