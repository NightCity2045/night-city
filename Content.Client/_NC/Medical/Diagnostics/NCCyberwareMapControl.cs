// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Input;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// An anatomical view of the disclosed scan only. Icon selection never reads live medical components.
/// Left and right refer to the patient, viewed from the front.
/// </summary>
public sealed partial class NCCyberwareMapControl : Control
{
    private static readonly Color Accent = Color.FromHex("#52D9CF");
    private static readonly Color Muted = Color.FromHex("#376568");
    private readonly NCAnatomyProjectionControl _body = new();
    private readonly List<BoxContainer> _groups = [];
    private readonly List<(GridContainer Grid, bool Nested)> _grids = [];
    private readonly List<WrapContainer> _contents = [];
    private readonly float[] _rowHeights = new float[4];
    private readonly List<(PanelContainer Panel, int Index, int Group, Color Color, bool Replacement)> _icons = [];
    private int _selected = -1;
    private readonly List<int> _spinalDevices = [];

    public event Action<int>? ModuleSelected;

    // Coordinates are normalized to the whole board, rather than the texture's pixel resolution.
    private static readonly Vector2[] Anchors =
    [
        new(.49f, .16f), new(.52f, .13f),
        new(.48f, .34f), new(.52f, .45f),
        new(.435f, .42f), new(.565f, .42f),
        new(.46f, .74f), new(.54f, .74f),
    ];

    public NCCyberwareMapControl()
    {
        MinSize = new Vector2(680, 430);
        HorizontalExpand = true;
        RectClipContent = true;
        AddChild(_body);
        AddChild(new Links(this) { MouseFilter = MouseFilterMode.Pass });
        for (var i = 0; i < Anchors.Length; i++)
        {
            var group = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 6,
            };
            group.AddChild(new Label
            {
                Text = Loc.GetString($"nc-cyberware-map-group-{i}"),
                FontColorOverride = Accent,
                ClipText = true,
            });
            var content = new WrapContainer
            {
                LayoutAxis = Axis.Horizontal,
                SeparationOverride = 8,
                CrossSeparationOverride = 8,
            };
            group.AddChild(content);
            _contents.Add(content);
            _groups.Add(group);
            AddChild(group);
        }
        AddChild(_skeleton);
    }

    public void Clear()
    {
        foreach (var content in _contents)
            content.RemoveAllChildren();
        _grids.Clear();
        _icons.Clear();
        _spinalDevices.Clear();
        _selected = -1;
        _skeleton.RemoveAllChildren();
        _skeletalSlots.Clear();
        Visible = false;
    }

    public void SetModules(IReadOnlyList<NCMedicalCyberwareSummary> modules,
        IReadOnlyList<NCSkeletalSlotPrototype> skeletalSlots,
        Func<NCMedicalCyberwareSummary, Texture?> icon,
        Func<NCMedicalCyberwareSummary, string> tooltip,
        Func<NCMedicalCyberwareSummary, Color> color)
    {
        Clear();
        _skeletonDefinitions = skeletalSlots;
        for (var i = 0; i < modules.Count; i++)
            if (modules[i].HostCategory?.Id == "Spine")
                _spinalDevices.Add(i);
        Visible = true;
        _body.SetProjection(new ProtoId<NCAnatomyProfilePrototype>("Human"), true, false);
        // A neural foundation is a separate visual root even when physically mounted inside a cybernetic head.
        // Otherwise a pod -> link -> device chain would hide the second-level devices in the regional layout.
        var neuralRoots = modules.Where(module => module.NeuralFoundationIndex != null)
            .Select(module => module.NeuralFoundationIndex!.Value).ToHashSet();
        var parents = new int?[modules.Count];
        for (var i = 0; i < modules.Count; i++)
        {
            if (neuralRoots.Contains(i))
                continue;
            foreach (var candidate in new[] { modules[i].NeuralFoundationIndex, modules[i].HostIndex })
            {
                if (candidate is not { } parent || parent < 0 || parent >= modules.Count || parent == i ||
                    GroupFor(modules[parent]) != GroupFor(modules[i]))
                    continue;
                parents[i] = parent;
                break;
            }
        }
        for (var group = 0; group < _groups.Count; group++)
        {
            var roots = new List<int>();
            for (var i = 0; i < modules.Count; i++)
            {
                if (GroupFor(modules[i]) != group)
                    continue;
                var parent = parents[i];
                if (parent == null || parent < 0 || parent >= modules.Count || parent == i ||
                    GroupFor(modules[parent.Value]) != group)
                    roots.Add(i);
            }

            var anatomical = NewColumn();
            var implants = NewGrid(false);
            foreach (var index in roots)
            {
                var module = modules[index];
                var hasModules = parents.Any(parent => parent == index);
                if (module.Form == NCCyberwareForm.Implant && !hasModules)
                {
                    implants.AddChild(CreateIcon(index, group, module, 42, icon, tooltip, color));
                    continue;
                }

                var basis = NewColumn();
                basis.AddChild(Caption("nc-cyberware-map-basis"));
                basis.AddChild(CreateIcon(index, group, module, 64, icon, tooltip, color));
                var descendants = NewGrid(true);
                for (var child = 0; child < modules.Count; child++)
                {
                    // Physical containment and neural dependencies are distinct server-disclosed edges.
                    if (parents[child] == index && GroupFor(modules[child]) == group)
                        descendants.AddChild(CreateIcon(child, group, modules[child], 42, icon, tooltip, color));
                }
                var node = new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    SeparationOverride = 8,
                };
                node.AddChild(basis);
                if (descendants.ChildCount > 0)
                {
                    node.AddChild(new OwnershipConnector());
                    var attached = NewColumn();
                    attached.AddChild(Caption(modules.Any(candidate => candidate.NeuralFoundationIndex == index)
                        ? "nc-cyberware-neural-connections" : "nc-cyberware-map-modules"));
                    attached.AddChild(descendants);
                    node.AddChild(attached);
                }
                _contents[group].AddChild(node);
            }
            if (implants.ChildCount > 0)
            {
                anatomical.AddChild(Caption("nc-cyberware-map-organ-implants"));
                anatomical.AddChild(implants);
                _contents[group].AddChild(anatomical);
            }
            if (_contents[group].ChildCount == 0)
                _contents[group].AddChild(new Label
                {
                    Text = Loc.GetString("nc-cyberware-map-no-modules"),
                    FontColorOverride = Muted,
                });
        }
        PopulateSkeleton(modules, icon, tooltip, color);
        InvalidateMeasure();
    }

    private static BoxContainer NewColumn() => new()
    {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        SeparationOverride = 4,
    };

    private static Label Caption(string key) => new()
    {
        Text = Loc.GetString(key), FontColorOverride = Muted,
    };

    private GridContainer NewGrid(bool nested)
    {
        var grid = new GridContainer { Columns = nested ? 2 : 4, HSeparationOverride = 4, VSeparationOverride = 4 };
        _grids.Add((grid, nested));
        return grid;
    }

    private PanelContainer CreateIcon(int index, int group, NCMedicalCyberwareSummary module, int size,
        Func<NCMedicalCyberwareSummary, Texture?> icon,
        Func<NCMedicalCyberwareSummary, string> tooltip,
        Func<NCMedicalCyberwareSummary, Color> color)
    {
        var stateColor = color(module);
        var panel = new PanelContainer { MinSize = new Vector2(size), PanelOverride = Frame(stateColor, false) };
        var button = new ContainerButton
        {
            ToolTip = tooltip(module), MinSize = new Vector2(size - 2),
            StyleBoxOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#07161D") },
        };
        button.AddChild(new TextureRect
        {
            Texture = icon(module), MinSize = new Vector2(size - 10), Margin = new Thickness(4),
            Stretch = TextureRect.StretchMode.KeepAspectCentered, MouseFilter = MouseFilterMode.Ignore,
        });
        button.OnPressed += _ => ModuleSelected?.Invoke(index);
        panel.AddChild(button);
        _icons.Add((panel, index, group, stateColor, module.Form == NCCyberwareForm.LimbReplacement));
        return panel;
    }

    public void Select(int index)
    {
        _selected = index;
        foreach (var item in _icons)
            item.Panel.PanelOverride = Frame(item.Color, item.Index == index);
    }

    private static StyleBoxFlat Frame(Color color, bool selected) => new()
    {
        BackgroundColor = selected ? Color.FromHex("#153A40") : Color.FromHex("#07161D"),
        BorderColor = selected ? Color.FromHex("#E4FFFC") : color,
        BorderThickness = new Thickness(selected ? 2 : 1),
    };

    private int GroupFor(NCMedicalCyberwareSummary module)
    {
        if (_skeletonDefinitions.Any(slot => slot.Category == module.HostCategory))
            return 8;
        var host = module.HostCategory?.Id ?? string.Empty;
        if (host is "Eyes") return 1;
        if (host is "ArmRight" or "HandRight") return 4;
        if (host is "ArmLeft" or "HandLeft") return 5;
        if (host is "LegRight" or "FootRight") return 6;
        if (host is "LegLeft" or "FootLeft") return 7;
        if (host is "Head" or "Brain" or "Spine") return 0;
        if (host is "Thorax" or "Heart") return 2;
        return 3;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var width = float.IsFinite(availableSize.X) ? Math.Max(680, availableSize.X) : 1120;
        var sideWidth = width * .27f - 24;
        foreach (var (grid, nested) in _grids)
        {
            var columns = Math.Clamp((int) ((sideWidth - (nested ? 84 : 0) + 4) / 46), 1, nested ? 3 : 4);
            if (grid.Columns != columns)
                grid.Columns = columns;
        }
        _body.Measure(availableSize);
        GetChild(1).Measure(availableSize);
        foreach (var group in _groups)
            group.Measure(new Vector2(sideWidth, float.PositiveInfinity));
        var height = 16f;
        for (var row = 0; row < 4; row++)
        {
            _rowHeights[row] = MathF.Max(96, MathF.Max(_groups[row * 2].DesiredSize.Y, _groups[row * 2 + 1].DesiredSize.Y) + 12);
            height += _rowHeights[row];
        }
        _boardHeight = height;
        _skeleton.Measure(new Vector2(width - 24, float.PositiveInfinity));
        return new Vector2(680, height + _skeleton.DesiredSize.Y + 12);
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var boardSize = new Vector2(finalSize.X, _boardHeight);
        _body.Arrange(UIBox2.FromDimensions(new Vector2(finalSize.X * .34f, 0), new Vector2(finalSize.X * .32f, boardSize.Y)));
        GetChild(1).Arrange(UIBox2.FromDimensions(Vector2.Zero, boardSize));
        _skeleton.Arrange(UIBox2.FromDimensions(new Vector2(12, boardSize.Y),
            new Vector2(finalSize.X - 24, _skeleton.DesiredSize.Y)));
        var extra = MathF.Max(0, (boardSize.Y - 16 - _rowHeights.Sum()) / 4);
        var y = 8f;
        for (var row = 0; row < 4; row++)
        {
            for (var side = 0; side < 2; side++)
            {
                var x = side == 0 ? 12 : finalSize.X * .73f;
                _groups[row * 2 + side].Arrange(UIBox2.FromDimensions(new Vector2(x, y),
                    new Vector2(finalSize.X * .27f - 24, _rowHeights[row] + extra - 6)));
            }
            y += _rowHeights[row] + extra;
        }
        return finalSize;
    }

    private sealed class OwnershipConnector : Control
    {
        public OwnershipConnector() { MinSize = new Vector2(14, 64); }
        protected override void Draw(DrawingHandleScreen handle)
        {
            var end = new Vector2(PixelWidth, PixelHeight * .65f);
            handle.DrawLine(new Vector2(0, end.Y), end, Accent);
            handle.DrawLine(end, end + new Vector2(-5, -4) * UIScale, Accent);
            handle.DrawLine(end, end + new Vector2(-5, 4) * UIScale, Accent);
        }
    }

    private sealed class Links(NCCyberwareMapControl owner) : Control
    {
        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);
            if (args.Function != EngineKeyFunctions.UIClick || Width <= 0 || Height <= 0)
                return;

            var point = args.RelativePosition / Size;
            if (point.X is < .3f or > .7f)
                return;

            var nearest = -1;
            var distance = float.MaxValue;
            foreach (var item in owner._icons)
            {
                if (item.Group >= Anchors.Length)
                    continue;
                var next = Vector2.DistanceSquared(point, Anchors[item.Group]);
                if (next >= distance)
                    continue;
                distance = next;
                nearest = item.Index;
            }
            foreach (var index in owner._spinalDevices)
            {
                var next = Vector2.DistanceSquared(point, new Vector2(.5f, .345f));
                if (next >= distance)
                    continue;
                distance = next;
                nearest = index;
            }
            foreach (var item in owner._skeletalSlots)
            {
                var next = Vector2.DistanceSquared(point, (item.Slot.MapStart + item.Slot.MapEnd) * .5f);
                if (next >= distance)
                    continue;
                distance = next;
                nearest = item.Index;
            }
            if (nearest >= 0 && distance < .025f)
            {
                owner.ModuleSelected?.Invoke(nearest);
                args.Handle();
            }
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            var size = PixelSize;
            for (var y = 12f; y < size.Y; y += 24 * UIScale)
                handle.DrawLine(new Vector2(size.X * .3f, y), new Vector2(size.X * .7f, y), Color.FromHex("#12303740"));
            foreach (var item in owner._icons)
            {
                if (!item.Replacement || item.Group < 4 || item.Group >= Anchors.Length)
                    continue;
                var sign = item.Group % 2 == 0 ? -1f : 1f;
                var arm = item.Group < 6;
                var a = new Vector2(.5f + sign * (arm ? .04f : .02f), arm ? .25f : .53f) * size;
                var b = new Vector2(.5f + sign * (arm ? .06f : .03f), arm ? .40f : .72f) * size;
                var c = new Vector2(.5f + sign * (arm ? .08f : .035f), arm ? .52f : .91f) * size;
                var color = item.Index == owner._selected ? Color.FromHex("#E4FFFC") : item.Color;
                handle.DrawLine(a, b, color);
                handle.DrawLine(b, c, color);
                handle.DrawCircle(a, 4 * UIScale, color);
                handle.DrawCircle(b, 4 * UIScale, color);
                handle.DrawCircle(c, 4 * UIScale, color);
            }
            foreach (var index in owner._spinalDevices)
            {
                var color = index == owner._selected ? Color.FromHex("#E4FFFC") : Accent;
                var start = new Vector2(.5f, .20f) * size;
                var end = new Vector2(.5f, .49f) * size;
                handle.DrawLine(start, end, color);
                if (index == owner._selected)
                    handle.DrawCircle((start + end) * .5f, 10 * UIScale, color, filled: false);
            }
            owner.DrawSkeleton(handle, size);
            for (var i = 0; i < Anchors.Length; i++)
            {
                var selected = owner._icons.Exists(item => item.Index == owner._selected && item.Group == i);
                var populated = owner._icons.Exists(item => item.Group == i);
                var color = selected ? Color.FromHex("#E4FFFC") : populated ? Accent : Muted.WithAlpha(.4f);
                var anchor = Anchors[i] * size;
                var end = new Vector2(size.X * (i % 2 == 0 ? .275f : .725f), (owner._groups[i].Position.Y + 48) * UIScale);
                var elbow = new Vector2(size.X * (i % 2 == 0 ? .32f : .68f), anchor.Y);
                handle.DrawLine(anchor, elbow, color);
                handle.DrawLine(elbow, end, color);
                handle.DrawCircle(anchor, selected ? 5 * UIScale : 3 * UIScale, color);
                if (selected)
                    handle.DrawCircle(anchor, 11 * UIScale, color, filled: false);
            }
        }
    }
}
