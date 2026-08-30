// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.Utility;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Draws the data-driven radiographic body projection disclosed by an internal scanner. The control owns no
/// medical state and never infers a diagnosis from pixels; visibility is decided from the server-filtered snapshot.
/// </summary>
public sealed partial class NCAnatomyProjectionControl : Control
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;

    private Texture? _atlas;
    private Vector2 _grid = new(2f, 2f);
    private Vector2 _skinCell;
    private Vector2 _projectionCell = new(1f, 0f);

    public NCAnatomyProjectionControl()
    {
        IoCManager.InjectDependencies(this);
        MinSize = new Vector2(180f, 270f);
        HorizontalExpand = true;
        MouseFilter = MouseFilterMode.Ignore;
        Visible = false;
    }

    public void SetProjection(
        ProtoId<NCAnatomyProfilePrototype> anatomyProfile,
        bool visible,
        bool includeOrgans)
    {
        Visible = visible;
        if (!visible)
            return;

        if (!_prototypes.TryIndex(anatomyProfile, out var anatomy) ||
            !_prototypes.TryIndex(anatomy.SurgicalField, out NCFreeformSurgeryProfilePrototype? surgery) ||
            !_resources.TryGetResource<TextureResource>(surgery.BodyLayerAtlas, out var atlas))
        {
            _atlas = null;
            return;
        }

        _atlas = atlas.Texture;
        _grid = surgery.BodyLayerAtlasGrid;
        _skinCell = surgery.BodySkinSprite;
        _projectionCell = includeOrgans
            ? surgery.BodyRadiographSprite
            : surgery.BodySkeletonSprite;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromHex("#030B0F"));
        if (_atlas == null)
            return;

        var columns = Math.Max(1f, _grid.X);
        var rows = Math.Max(1f, _grid.Y);
        var cellSize = new Vector2(_atlas.Width / columns, _atlas.Height / rows);
        var scale = MathF.Min(PixelWidth / cellSize.X, PixelHeight / cellSize.Y);
        var destinationSize = cellSize * MathF.Max(0.01f, scale);
        var destination = UIBox2.FromDimensions((PixelSize - destinationSize) * 0.5f, destinationSize);

        DrawCell(handle, _projectionCell, destination, Color.FromHex("#A8DFFF"));
        // NC: A faint skin pass produces the readable translucent silhouette from the scanner reference while
        // leaving the authoritative bone/organ projection dominant.
        DrawCell(handle, _skinCell, destination, Color.FromHex("#45A7FF2E"));
    }

    private void DrawCell(DrawingHandleScreen handle, Vector2 cell, UIBox2 destination, Color color)
    {
        if (_atlas == null)
            return;
        var columns = Math.Max(1f, _grid.X);
        var rows = Math.Max(1f, _grid.Y);
        var cellSize = new Vector2(_atlas.Width / columns, _atlas.Height / rows);
        var source = UIBox2.FromDimensions(cell * cellSize, cellSize);
        handle.DrawTextureRectRegion(_atlas, destination, source, color);
    }
}
