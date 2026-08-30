// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// Stable identity of one lightweight client-side surface node. Zero is never allocated.
/// </summary>
public readonly record struct UiNodeId(uint Value)
{
    public static readonly UiNodeId Invalid = new(0);

    public bool IsValid => Value != 0;

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Compact handle into the immutable visual registry.
/// </summary>
public readonly record struct UiVisualHandle(uint Value)
{
    public static readonly UiVisualHandle Invalid = new(0);
    public bool IsValid => Value != 0;
}

/// <summary>
/// Compact handle into the hit-shape registry. Invalid means rectangular bounds.
/// </summary>
public readonly record struct UiHitShapeHandle(uint Value)
{
    public static readonly UiHitShapeHandle Invalid = new(0);
    public bool IsValid => Value != 0;
}

/// <summary>
/// Presenter-owned action identity used without reflection or string dispatch.
/// </summary>
public readonly record struct UiActionId(uint Value)
{
    public static readonly UiActionId Invalid = new(0);
    public bool IsValid => Value != 0;
}
