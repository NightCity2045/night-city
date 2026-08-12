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
    private readonly float _displayScale;

    public NCTargetZoneButton(
        ProtoId<NCTargetZonePrototype> zone,
        Texture selectedTexture,
        Texture hoverTexture,
        NCTextureRegion? selectedRegion,
        NCTextureRegion? hoverRegion,
        float displayScale)
    {
        Zone = zone;
        _selectedTexture = selectedTexture;
        _hoverTexture = hoverTexture;
        _selectedRegion = ToBox(selectedRegion);
        _hoverRegion = ToBox(hoverRegion);
        _displayScale = displayScale;

        var selectedSize = _selectedRegion?.Size ?? selectedTexture.Size;
        var hoverSize = _hoverRegion?.Size ?? hoverTexture.Size;
        SetSize = Vector2.Max(selectedSize, hoverSize) * displayScale;
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
        var sourceSize = source?.Size ?? texture.Size;
        var drawSize = sourceSize * _displayScale * UIScale;
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
