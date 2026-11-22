// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Shared._NC.Skill;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._NC.Skill;

public sealed class NCSkillGraph : Control
{
    private readonly IPrototypeManager _prototypes = IoCManager.Resolve<IPrototypeManager>();
    private readonly SpriteSystem _sprites = IoCManager.Resolve<IEntityManager>().System<SpriteSystem>();
    private readonly Font _font;
    private Vector2 _pan;
    private float _zoom = 1;
    private Vector2 _previous;
    private string? _drag;
    private bool _panning;

    public IList<NCSkillNode> Nodes { get; set; } = new List<NCSkillNode>();
    public IReadOnlySet<string> Learned { get; set; } = new HashSet<string>();
    public string? Selected { get; set; }
    public bool Editing { get; set; }
    public bool Connecting { get; set; }
    public event Action<string>? Picked;

    public NCSkillGraph()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/EngineFonts/NotoSans/NotoSans-Regular.ttf"), 12);
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
    }

    public void Center()
    {
        _zoom = 1;
        _pan = Nodes.Count == 0
            ? Vector2.Zero
            : -Nodes.Select(node => node.Position).Aggregate(Vector2.Zero, (sum, value) => sum + value)
                / Nodes.Count * Scale;
    }

    private float Scale => 64 * _zoom * UIScale;
    private Vector2 Origin => PixelSize / 2 + _pan;
    private Vector2 Cursor => UserInterfaceManager.MousePositionScaled.Position * UIScale - GlobalPixelPosition;
    private Vector2 NodePosition(NCSkillNode node) => Origin + node.Position * Scale;
    private float Radius => 23 * UIScale;

    protected override void Draw(DrawingHandleScreen handle)
    {
        handle.DrawRect(new UIBox2(0, 0, PixelWidth, PixelHeight), Color.FromHex("#172027"));
        var positions = Nodes.ToDictionary(node => node.Id, NodePosition);
        foreach (var node in Nodes)
        {
            DrawDependencies(handle, node.Prerequisites, positions, positions[node.Id], Color.FromHex("#8FA5B4"));
            foreach (var group in node.AnyPrerequisites)
                DrawDependencies(handle, group, positions, positions[node.Id], Color.FromHex("#E8AC60"));
        }

        foreach (var node in Nodes)
        {
            var pos = positions[node.Id];
            var rect = new UIBox2(pos - new Vector2(Radius), pos + new Vector2(Radius));
            var color = Selected == node.Id ? Color.FromHex("#F8BC66")
                : Learned.Contains(node.Id) ? Color.FromHex("#78CCAD") : Color.FromHex("#78919D");
            handle.DrawRect(rect, color);
            var inset = new Vector2(Radius - 2 * UIScale);
            handle.DrawRect(new UIBox2(pos - inset, pos + inset), Color.FromHex("#28373D"));

            if (!_prototypes.TryIndex<NCSkillPrototype>(node.Id, out var skill))
                continue;

            var icon = _sprites.Frame0(skill.Icon);
            var half = new Vector2(Radius - 4 * UIScale);
            if (icon.Width > icon.Height)
                half.Y *= (float) icon.Height / icon.Width;
            else
                half.X *= (float) icon.Width / icon.Height;

            handle.DrawTextureRect(icon, new UIBox2(pos - half, pos + half));
            handle.DrawString(_font, pos + new Vector2(-Radius, Radius + 4 * UIScale),
                Loc.GetString(skill.Name), UIScale, Color.White);
        }
    }

    private void DrawDependencies(DrawingHandleScreen handle, IEnumerable<string> prerequisites,
        Dictionary<string, Vector2> positions, Vector2 end, Color color)
    {
        foreach (var id in prerequisites)
        {
            if (!positions.TryGetValue(id, out var start) || Vector2.DistanceSquared(start, end) <= Radius * Radius * 4)
                continue;

            var direction = Vector2.Normalize(end - start);
            var side = new Vector2(-direction.Y, direction.X);
            var tip = end - direction * (Radius + 4 * UIScale);
            handle.DrawLine(start, tip, color);
            handle.DrawLine(tip, tip - direction * 7 * UIScale + side * 4 * UIScale, color);
            handle.DrawLine(tip, tip - direction * 7 * UIScale - side * 4 * UIScale, color);
        }
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        var before = (Cursor - Origin) / Scale;
        _zoom = Math.Clamp(_zoom * MathF.Pow(1.15f, args.Delta.Y), 0.35f, 2.5f);
        _pan = Cursor - PixelSize / 2 - before * Scale;
        args.Handle();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        if (args.Function != EngineKeyFunctions.UIClick || args.IsRepeat)
            return;

        _previous = Cursor;
        var node = Nodes.Reverse().FirstOrDefault(candidate =>
        {
            var delta = Vector2.Abs(NodePosition(candidate) - _previous);
            return delta.X <= Radius && delta.Y <= Radius;
        });

        if (node != null)
        {
            _drag = Editing && !Connecting ? node.Id : null;
            Picked?.Invoke(node.Id);
        }
        else
        {
            _panning = true;
        }

        args.Handle();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (!_panning && _drag == null)
            return;

        var delta = Cursor - _previous;
        if (_panning)
            _pan += delta;
        else
        {
            for (var i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                if (node.Id != _drag)
                    continue;

                Nodes[i] = node with { Position = node.Position + delta / Scale };
                break;
            }
        }

        _previous = Cursor;
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        _drag = null;
        _panning = false;
        args.Handle();
    }

    protected override void ExitedTree()
    {
        _drag = null;
        _panning = false;
        base.ExitedTree();
    }
}
