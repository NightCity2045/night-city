// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.Animation;

public enum UiAnimationProperty : byte
{
    X,
    Y,
    Width,
    Height,
    Opacity,
    Rotation,
    Scale,
    TintRed,
    TintGreen,
    TintBlue,
    TintAlpha,
    Highlight,
    Thickness,
    Fill,
}

public enum UiEasing : byte
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>
/// Value animation for one lightweight node. Only active entries are updated each frame.
/// </summary>
public struct UiNodeAnimation
{
    public UiNodeId Node;
    public UiAnimationProperty Property;
    public float StartValue;
    public float EndValue;
    public float Duration;
    public float Elapsed;
    public UiEasing Easing;
}
