// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Animation;

/// <summary>
/// Central active-animation list with swap-back completion and no per-node FrameUpdate calls.
/// </summary>
public sealed class UiAnimationScheduler
{
    private readonly UiSurfaceDocument _document;
    private readonly List<UiNodeAnimation> _active = [];

    public int ActiveCount => _active.Count;
    public event Action<UiNodeId, UiAnimationProperty>? Completed;

    public UiAnimationScheduler(UiSurfaceDocument document)
    {
        _document = document;
        _document.NodeRemoved += Cancel;
        _document.Cleared += Clear;
    }

    public void Start(UiNodeAnimation animation)
    {
        if (!_document.Nodes.Contains(animation.Node))
            throw new ArgumentException($"Unknown NCUI node {animation.Node}.", nameof(animation));
        animation.Duration = Math.Max(0.0001f, animation.Duration);
        animation.Elapsed = 0f;
        for (var i = 0; i < _active.Count; i++)
        {
            if (_active[i].Node == animation.Node && _active[i].Property == animation.Property)
            {
                _active[i] = animation;
                return;
            }
        }
        _active.Add(animation);
    }

    public void Update(float frameTime)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var animation = _active[i];
            if (!_document.Nodes.TryGet(animation.Node, out var node))
            {
                RemoveAt(i);
                continue;
            }
            animation.Elapsed = Math.Min(animation.Duration, animation.Elapsed + Math.Max(0f, frameTime));
            var progress = Ease(animation.Elapsed / animation.Duration, animation.Easing);
            Apply(ref node, animation.Property, MathHelper.Lerp(animation.StartValue, animation.EndValue, progress));
            _document.UpdateNode(animation.Node, node);
            if (animation.Elapsed >= animation.Duration)
            {
                RemoveAt(i);
                Completed?.Invoke(animation.Node, animation.Property);
            }
            else
            {
                _active[i] = animation;
            }
        }
    }

    public void Cancel(UiNodeId node)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Node == node)
                RemoveAt(i);
        }
    }

    public void Clear() => _active.Clear();

    private void RemoveAt(int index)
    {
        var last = _active.Count - 1;
        _active[index] = _active[last];
        _active.RemoveAt(last);
    }

    private static float Ease(float value, UiEasing easing)
    {
        value = Math.Clamp(value, 0f, 1f);
        return easing switch
        {
            UiEasing.EaseIn => value * value,
            UiEasing.EaseOut => 1f - (1f - value) * (1f - value),
            UiEasing.EaseInOut => value < 0.5f
                ? 2f * value * value
                : 1f - MathF.Pow(-2f * value + 2f, 2f) * 0.5f,
            _ => value,
        };
    }

    private static void Apply(ref UiSurfaceNode node, UiAnimationProperty property, float value)
    {
        var bounds = node.Bounds;
        switch (property)
        {
            case UiAnimationProperty.X:
                node.Bounds = UIBox2.FromDimensions(new Vector2(value, bounds.Top), bounds.Size);
                break;
            case UiAnimationProperty.Y:
                node.Bounds = UIBox2.FromDimensions(new Vector2(bounds.Left, value), bounds.Size);
                break;
            case UiAnimationProperty.Width:
                node.Bounds = UIBox2.FromDimensions(bounds.TopLeft, new Vector2(Math.Max(0f, value), bounds.Height));
                break;
            case UiAnimationProperty.Height:
                node.Bounds = UIBox2.FromDimensions(bounds.TopLeft, new Vector2(bounds.Width, Math.Max(0f, value)));
                break;
            case UiAnimationProperty.Opacity:
                node.Opacity = Math.Clamp(value, 0f, 1f);
                break;
            case UiAnimationProperty.Rotation:
                node.Rotation = value;
                break;
            case UiAnimationProperty.Scale:
                node.Scale = Math.Max(0f, value);
                break;
            case UiAnimationProperty.TintRed:
                node.Tint = node.Tint.WithRed(Math.Clamp(value, 0f, 1f));
                break;
            case UiAnimationProperty.TintGreen:
                node.Tint = node.Tint.WithGreen(Math.Clamp(value, 0f, 1f));
                break;
            case UiAnimationProperty.TintBlue:
                node.Tint = node.Tint.WithBlue(Math.Clamp(value, 0f, 1f));
                break;
            case UiAnimationProperty.TintAlpha:
                node.Tint = node.Tint.WithAlpha(Math.Clamp(value, 0f, 1f));
                break;
            case UiAnimationProperty.Highlight:
                node.Highlight = Math.Clamp(value, 0f, 1f);
                break;
            case UiAnimationProperty.Thickness:
                node.ThicknessScale = Math.Max(0f, value);
                break;
            case UiAnimationProperty.Fill:
                node.Fill = Math.Clamp(value, 0f, 1f);
                break;
        }
    }
}
