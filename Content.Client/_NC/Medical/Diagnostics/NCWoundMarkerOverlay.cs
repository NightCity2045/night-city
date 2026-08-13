// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Draws capability-filtered wound marks on the diagnostic doll. It receives no wound identifiers and
/// derives every pixel from the immutable, server-sanitized impact coordinates in the snapshot.
/// </summary>
public sealed class NCWoundMarkerOverlay : Control
{
    // A regular array is intentional. ReadOnlySpan collection expressions compile to by-ref inline-array
    // helpers that are rejected by the RobustToolbox content sandbox verifier.
    private static readonly (int X, int Y)[] CollisionOffsets = new (int X, int Y)[]
    {
        (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (-1, 1), (1, -1), (-1, -1),
        (2, 0), (-2, 0), (0, 2), (0, -2),
    };

    private readonly Dictionary<ProtoId<NCTargetZonePrototype>, UIBox2> _zoneBounds = [];
    private readonly List<NCMedicalWoundSummary> _wounds = [];
    private float _displayScale = 1f;

    public NCWoundMarkerOverlay()
    {
        MouseFilter = MouseFilterMode.Ignore;
    }

    public void Configure(
        IReadOnlyDictionary<ProtoId<NCTargetZonePrototype>, UIBox2> zoneBounds,
        Vector2 canvasSize,
        float displayScale)
    {
        _zoneBounds.Clear();
        foreach (var (zone, bounds) in zoneBounds)
            _zoneBounds.Add(zone, bounds);

        _displayScale = displayScale;
        SetSize = canvasSize * displayScale;
    }

    public void SetWounds(IReadOnlyList<NCMedicalWoundSummary> wounds)
    {
        _wounds.Clear();
        for (var i = 0; i < wounds.Count; i++)
            _wounds.Add(wounds[i]);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (_wounds.Count == 0)
            return;

        var occupied = new HashSet<(ProtoId<NCTargetZonePrototype> Zone, int X, int Y)>();
        foreach (var wound in _wounds)
        {
            if (!_zoneBounds.TryGetValue(wound.Zone, out var bounds))
                continue;

            var point = ResolvePoint(wound, bounds, occupied);
            DrawWound(handle, wound, bounds, point);
        }
    }

    private static Vector2 ResolvePoint(
        NCMedicalWoundSummary wound,
        UIBox2 bounds,
        HashSet<(ProtoId<NCTargetZonePrototype> Zone, int X, int Y)> occupied)
    {
        var width = Math.Max(1, (int) MathF.Floor(bounds.Width));
        var height = Math.Max(1, (int) MathF.Floor(bounds.Height));
        var x = Math.Clamp((int) MathF.Floor(wound.VisualX / 256f * width), 0, width - 1);
        var y = Math.Clamp((int) MathF.Floor(wound.VisualY / 256f * height), 0, height - 1);

        // Several impacts can legitimately quantize to one sprite pixel. A small deterministic spiral keeps
        // each bounded wound visible without inventing a new random location on every refresh.
        foreach (var offset in CollisionOffsets)
        {
            var candidateX = Math.Clamp(x + offset.X, 0, width - 1);
            var candidateY = Math.Clamp(y + offset.Y, 0, height - 1);
            if (!occupied.Add((wound.Zone, candidateX, candidateY)))
                continue;

            return new Vector2(bounds.Left + candidateX, bounds.Top + candidateY);
        }

        return new Vector2(bounds.Left + x, bounds.Top + y);
    }

    private void DrawWound(
        DrawingHandleScreen handle,
        NCMedicalWoundSummary wound,
        UIBox2 bounds,
        Vector2 point)
    {
        var color = WoundColor(wound);
        switch (wound.Type)
        {
            case NCWoundType.Slash:
                DrawSlash(handle, bounds, point, wound.VisualDirection, wound.Severity, color);
                return;
            case NCWoundType.Blunt:
            case NCWoundType.Pressure:
                DrawCell(handle, bounds, point, color.WithAlpha(0.78f));
                DrawCell(handle, bounds, point + Vector2.UnitX, color.WithAlpha(0.58f));
                DrawCell(handle, bounds, point + Vector2.UnitY, color.WithAlpha(0.58f));
                return;
            case NCWoundType.Thermal:
            case NCWoundType.Cold:
            case NCWoundType.Chemical:
            case NCWoundType.Blast:
                DrawCell(handle, bounds, point, color);
                DrawCell(handle, bounds, point + new Vector2(1f, 0f), color.WithAlpha(0.7f));
                DrawCell(handle, bounds, point + new Vector2(0f, 1f), color.WithAlpha(0.7f));
                return;
            case NCWoundType.Electrical:
                DrawCell(handle, bounds, point, color);
                DrawCell(handle, bounds, point + new Vector2(1f, 1f), color.WithAlpha(0.75f));
                return;
            default:
                // Ballistic and puncture wounds intentionally remain one source pixel per impact.
                DrawCell(handle, bounds, point, color);
                return;
        }
    }

    private void DrawSlash(
        DrawingHandleScreen handle,
        UIBox2 bounds,
        Vector2 center,
        byte direction,
        NCWoundSeverity severity,
        Color color)
    {
        var angle = direction / 255f * MathF.Tau;
        var step = new Vector2(
            MathF.Abs(MathF.Cos(angle)) >= 0.35f ? MathF.Sign(MathF.Cos(angle)) : 0f,
            MathF.Abs(MathF.Sin(angle)) >= 0.35f ? MathF.Sign(MathF.Sin(angle)) : 0f);
        if (step == Vector2.Zero)
            step = Vector2.UnitX;

        var radius = severity >= NCWoundSeverity.Critical ? 2 : 1;
        for (var i = -radius; i <= radius; i++)
            DrawCell(handle, bounds, center + step * i, color);
    }

    private void DrawCell(DrawingHandleScreen handle, UIBox2 bounds, Vector2 point, Color color)
    {
        if (point.X < bounds.Left || point.Y < bounds.Top ||
            point.X >= bounds.Right || point.Y >= bounds.Bottom)
        {
            return;
        }

        var scale = _displayScale * UIScale;
        var pixelPoint = point * scale;
        handle.DrawRect(UIBox2.FromDimensions(pixelPoint, new Vector2(scale, scale)), color);
    }

    private static Color WoundColor(NCMedicalWoundSummary wound)
    {
        if (wound.Stage == NCWoundStage.Scarred)
            return Color.FromHex("#C68D92");
        if (wound.Stage is NCWoundStage.Treated or NCWoundStage.Closed or NCWoundStage.Healing)
            return Color.FromHex("#B85D65");

        return wound.Type switch
        {
            NCWoundType.Thermal => Color.FromHex("#FF8A3D"),
            NCWoundType.Electrical => Color.FromHex("#FFD84A"),
            NCWoundType.Blast => Color.FromHex("#FF493D"),
            NCWoundType.Cold => Color.FromHex("#79D7FF"),
            NCWoundType.Pressure => Color.FromHex("#B57AFF"),
            NCWoundType.Chemical => Color.FromHex("#B6E447"),
            NCWoundType.Blunt => Color.FromHex("#A93F78"),
            NCWoundType.Pierce => Color.FromHex("#FF4F69"),
            _ => Color.FromHex("#FF2738"),
        };
    }
}
