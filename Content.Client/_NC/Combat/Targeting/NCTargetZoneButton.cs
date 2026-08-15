// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Combat.Targeting;

/// <summary>
/// Pixel-art zone button. Optional source regions let chest and abdomen reuse different halves
/// of the supplied torso textures without creating or modifying third-party assets.
/// </summary>
public sealed class NCTargetZoneButton : TextureButton
{
    public ProtoId<NCTargetZonePrototype> Zone { get; }

    private readonly Texture _selectedTexture;
    private readonly Texture _hoverTexture;
    private readonly UIBox2? _selectedRegion;
    private readonly UIBox2? _hoverRegion;
    private readonly UIBox2? _interactionRegion;
    private readonly Vector2 _displaySize;
    private readonly Vector2 _hitboxPadding;
    private readonly float _displayScale;

    public NCTargetZoneButton(
        ProtoId<NCTargetZonePrototype> zone,
        Texture selectedTexture,
        Texture hoverTexture,
        NCTextureRegion? selectedRegion,
        NCTextureRegion? hoverRegion,
        Vector2? displaySize,
        Vector2 hitboxPadding,
        NCTextureRegion? interactionRegion,
        float displayScale)
    {
        Zone = zone;
        _selectedTexture = selectedTexture;
        _hoverTexture = hoverTexture;
        _selectedRegion = ToBox(selectedRegion);
        _hoverRegion = ToBox(hoverRegion);
        _interactionRegion = ToBox(interactionRegion);
        _hitboxPadding = hitboxPadding;
        _displayScale = displayScale;

        var selectedSize = _selectedRegion?.Size ?? selectedTexture.Size;
        var hoverSize = _hoverRegion?.Size ?? hoverTexture.Size;
        _displaySize = displaySize ?? Vector2.Max(selectedSize, hoverSize);
        SetSize = (_displaySize + hitboxPadding * 2f) * displayScale;
    }

    protected override bool HasPoint(Vector2 point)
    {
        if (_interactionRegion is not { } region)
            return base.HasPoint(point);

        var origin = (_hitboxPadding + region.TopLeft) * _displayScale;
        var size = region.Size * _displayScale;
        return point.X >= origin.X && point.Y >= origin.Y &&
               point.X < origin.X + size.X && point.Y < origin.Y + size.Y;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        if (Pressed)
        {
            DrawCentered(handle, _selectedTexture, _selectedRegion);
            return;
        }

        if (IsHovered || DrawMode == DrawModeEnum.Pressed)
            DrawCentered(handle, _hoverTexture, _hoverRegion);
    }

    private void DrawCentered(DrawingHandleScreen handle, Texture texture, UIBox2? source)
    {
        var drawSize = _displaySize * _displayScale * UIScale;
        var drawPosition = (PixelSize - drawSize) / 2f;
        handle.DrawTextureRectRegion(
            texture,
            UIBox2.FromDimensions(drawPosition, drawSize),
            source,
            Color.White);
    }

    private static UIBox2? ToBox(NCTextureRegion? region)
    {
        return region == null ? null : UIBox2.FromDimensions(region.Position, region.Size);
    }
}
