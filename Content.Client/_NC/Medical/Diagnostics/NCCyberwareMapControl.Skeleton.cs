// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Medical.Diagnostics;

public sealed partial class NCCyberwareMapControl
{
    private readonly BoxContainer _skeleton = new()
    {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        SeparationOverride = 6,
    };
    private IReadOnlyList<NCSkeletalSlotPrototype> _skeletonDefinitions = Array.Empty<NCSkeletalSlotPrototype>();
    private readonly List<(NCSkeletalSlotPrototype Slot, int Index, Color Color)> _skeletalSlots = [];
    private float _boardHeight;

    private void PopulateSkeleton(IReadOnlyList<NCMedicalCyberwareSummary> modules,
        Func<NCMedicalCyberwareSummary, Texture?> icon,
        Func<NCMedicalCyberwareSummary, string> tooltip,
        Func<NCMedicalCyberwareSummary, Color> color)
    {
        _skeleton.AddChild(new Label
        {
            Text = Loc.GetString("nc-skeleton-map-title"), FontColorOverride = Accent,
        });
        var hint = new RichTextLabel { HorizontalExpand = true };
        hint.SetMessage(Loc.GetString("nc-skeleton-map-hint"), Muted);
        _skeleton.AddChild(hint);
        if (_skeletalEnhancements.Count > 0)
        {
            _skeleton.AddChild(new Label
            {
                Text = Loc.GetString("nc-skeletal-diagnostics-layers"), FontColorOverride = Accent,
            });
            var enhancements = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, SeparationOverride = 8 };
            for (var i = 0; i < modules.Count; i++)
            {
                if (!_skeletalEnhancements.Contains(i)) continue;
                var device = CreateIcon(i, 9, modules[i], 52, icon, tooltip, color);
                device.MaxSize = new Vector2(52);
                enhancements.AddChild(device);
            }
            _skeleton.AddChild(enhancements);
        }
        var tiles = new WrapContainer
        {
            LayoutAxis = Axis.Horizontal, SeparationOverride = 8, CrossSeparationOverride = 8,
        };
        foreach (var slot in _skeletonDefinitions)
        {
            var tile = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 4, MinWidth = 130, MaxWidth = 130,
            };
            var title = new RichTextLabel { HorizontalExpand = true, MinHeight = 42 };
            title.SetMessage(Loc.GetString(slot.Name), Accent);
            tile.AddChild(title);
            var found = false;
            for (var index = 0; index < modules.Count; index++)
            {
                var module = modules[index];
                if (module.HostCategory != slot.Category)
                    continue;
                var device = CreateIcon(index, 8, module, 52, icon, tooltip, color);
                device.MaxSize = new Vector2(52);
                tile.AddChild(device);
                _skeletalSlots.Add((slot, index, color(module)));
                found = true;
            }
            if (!found)
            {
                var frame = new PanelContainer
                {
                    MinSize = new Vector2(52), MaxSize = new Vector2(52),
                    MouseFilter = MouseFilterMode.Pass,
                    ToolTip = Loc.GetString("nc-skeleton-map-undetected-hint"),
                    PanelOverride = Frame(Muted.WithAlpha(.5f), false),
                };
                frame.AddChild(new BoneGlyph(slot.Order));
                tile.AddChild(frame);
                tile.AddChild(new Label
                {
                    Text = Loc.GetString("nc-skeleton-map-undetected"), FontColorOverride = Muted,
                });
            }
            tiles.AddChild(tile);
        }
        _skeleton.AddChild(tiles);
    }

    private void DrawSkeleton(DrawingHandleScreen handle, Vector2 size)
    {
        foreach (var item in _skeletalSlots)
        {
            var selected = item.Index == _selected;
            var color = selected ? Color.FromHex("#E4FFFC") : item.Color;
            var start = item.Slot.MapStart * size;
            var end = item.Slot.MapEnd * size;
            handle.DrawLine(start, end, color);
            handle.DrawCircle(start, 4 * UIScale, color);
            handle.DrawCircle(end, 4 * UIScale, color);
            if (selected)
                handle.DrawCircle((start + end) * .5f, 10 * UIScale, color, filled: false);
        }
    }

    /// <summary>Neutral anatomical symbols for undisclosed replacement sites; actual devices use their sprite.</summary>
    private sealed class BoneGlyph(int kind) : Control
    {
        protected override void Draw(DrawingHandleScreen handle)
        {
            var size = PixelSize;
            var color = Muted;
            void Line(float x1, float y1, float x2, float y2) =>
                handle.DrawLine(new Vector2(x1, y1) * size, new Vector2(x2, y2) * size, color);
            if (kind == 0)
            {
                handle.DrawCircle(size * new Vector2(.5f, .4f), size.X * .23f, color, filled: false);
                Line(.35f, .58f, .35f, .78f);
                Line(.35f, .78f, .65f, .78f);
                Line(.65f, .78f, .65f, .58f);
                handle.DrawCircle(size * new Vector2(.42f, .4f), size.X * .04f, color);
                handle.DrawCircle(size * new Vector2(.58f, .4f), size.X * .04f, color);
            }
            else if (kind == 1)
            {
                Line(.5f, .18f, .5f, .82f);
                for (var y = .3f; y < .8f; y += .15f)
                {
                    Line(.22f, y, .5f, y + .1f);
                    Line(.5f, y + .1f, .78f, y);
                }
            }
            else if (kind == 2)
            {
                Line(.2f, .3f, .35f, .72f);
                Line(.35f, .72f, .65f, .72f);
                Line(.65f, .72f, .8f, .3f);
                Line(.2f, .3f, .5f, .48f);
                Line(.5f, .48f, .8f, .3f);
            }
            else if (kind == 7)
            {
                Line(.5f, .15f, .5f, .85f);
                for (var y = .2f; y < .85f; y += .12f)
                    Line(.35f, y, .65f, y);
            }
            else
            {
                Line(.5f, .25f, .5f, .75f);
                for (var y = .23f; y < .8f; y += .54f)
                {
                    handle.DrawCircle(size * new Vector2(.43f, y), size.X * .08f, color, filled: false);
                    handle.DrawCircle(size * new Vector2(.57f, y), size.X * .08f, color, filled: false);
                }
            }
        }
    }
}
