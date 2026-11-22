// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using System.Text;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Input;

namespace Content.Client._NC.Medical.Pharmacology;

/// <summary>One node of the formula tree as the canvas draws it.</summary>
public sealed class NCPharmaTreeNode(string id, NCPharmaNodeBranch branch, string title)
{
    public readonly string Id = id;
    public readonly NCPharmaNodeBranch Branch = branch;
    public readonly string Title = title;

    /// <summary>Taken into the formula being built.</summary>
    public bool Chosen;

    /// <summary>What the laboratory knows about it: untested nodes are drawn with a question mark.</summary>
    public NCPharmaNodeState Knowledge = NCPharmaNodeState.Confirmed;

    /// <summary>Cannot be chosen yet, for example a compound nobody has identified.</summary>
    public bool Locked;

    internal Vector2 World;
}

/// <summary>
/// The formula tree as a map: a hub in the middle, five branches around it and the nodes of each branch on arcs
/// beyond it, joined to their branch by lines. Drag to move, wheel to zoom, click a node to take it into the
/// formula or drop it again.
/// </summary>
public sealed partial class NCPharmaTreeCanvas : Control
{
    private const float HubRadius = 46f;
    private const float BranchRadius = 36f;
    private const float NodeRadius = 24f;
    private const int LabelWidth = 16;

    private static readonly Color Background = Color.FromHex("#10191D");
    private static readonly Color Line = Color.FromHex("#3A4A52");
    private static readonly Color SuspectedColor = Color.FromHex("#FFC95C");
    private static readonly Color UnknownColor = Color.FromHex("#7A8A8C");

    [Dependency] private IResourceCache _resources = default!;

    private readonly List<NCPharmaTreeNode> _nodes = [];
    private readonly Vector2[] _branchPositions = new Vector2[5];
    private Font _font = default!;
    private Font _bold = default!;
    private float _zoom = 1f;
    private Vector2 _pan;
    private bool _panning;
    private bool _moved;
    private bool _fit = true;
    private float _extent = 600f;
    private NCPharmaTreeNode? _hover;
    private string _hubTitle = string.Empty;

    public event Action<string>? NodePressed;
    public event Action<string?>? NodeHovered;
    public event Action? HubPressed;

    public NCPharmaTreeCanvas()
    {
        IoCManager.InjectDependencies(this);
        MinSize = new Vector2(160, 200);
        HorizontalExpand = true;
        VerticalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
        _font = new VectorFont(_resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
        _bold = new VectorFont(_resources.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 13);
    }

    public string HubTitle
    {
        get => _hubTitle;
        set => _hubTitle = value;
    }

    public static Color BranchColor(NCPharmaNodeBranch branch) => branch switch
    {
        NCPharmaNodeBranch.Core => Color.FromHex("#58E6D9"),
        NCPharmaNodeBranch.Delivery => Color.FromHex("#7FB6FF"),
        NCPharmaNodeBranch.Kinetics => Color.FromHex("#C79BFF"),
        NCPharmaNodeBranch.Safety => Color.FromHex("#FF8A8A"),
        _ => Color.FromHex("#FFC95C"),
    };

    public void SetNodes(IReadOnlyList<NCPharmaTreeNode> nodes)
    {
        _nodes.Clear();
        _nodes.AddRange(nodes);
        _hover = null;

        var branches = Enum.GetValues<NCPharmaNodeBranch>();
        var groups = branches.Select(branch => _nodes.Where(node => node.Branch == branch).ToList()).ToList();
        var (positions, children, extent) = NCPharmaTreeLayout.Compute(groups.Select(group => group.Count).ToList());
        for (var branch = 0; branch < branches.Length; branch++)
        {
            _branchPositions[branch] = positions[branch];
            for (var i = 0; i < groups[branch].Count; i++)
                groups[branch][i].World = children[branch][i];
        }

        _extent = extent;
        _fit = true;
    }

    public void ResetView() => _fit = true;

    private Vector2 Center => PixelSize / 2f + _pan;

    private Vector2 ToScreen(Vector2 world) => Center + world * _zoom * UIScale;

    private float Scaled(float design) => design * _zoom * UIScale;

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (_fit && PixelWidth > 0 && PixelHeight > 0)
        {
            // Readable first: the whole map may not fit, and dragging and the wheel reach the rest.
            _zoom = Math.Clamp(MathF.Min(PixelWidth, PixelHeight) / UIScale / (_extent * 2f), 0.6f, 1.1f);
            _pan = Vector2.Zero;
            _fit = false;
        }

        handle.DrawRect(PixelSizeBox, Background);
        var hub = ToScreen(Vector2.Zero);

        foreach (var branch in Enum.GetValues<NCPharmaNodeBranch>())
        {
            var color = BranchColor(branch);
            var branchPosition = ToScreen(_branchPositions[(int) branch]);
            handle.DrawLine(hub, branchPosition, color.WithAlpha(0.7f));
            foreach (var node in _nodes.Where(node => node.Branch == branch))
            {
                var position = ToScreen(node.World);
                handle.DrawLine(branchPosition, position, node.Chosen ? color : Line);
            }
        }

        DrawNode(handle, hub, Scaled(HubRadius), Color.FromHex("#2B3A40"), Color.White, _hubTitle, true, false, NCPharmaNodeState.Confirmed);
        foreach (var branch in Enum.GetValues<NCPharmaNodeBranch>())
        {
            var color = BranchColor(branch);
            var title = Loc.GetString($"nc-pharma-rd-branch-{branch.ToString().ToLowerInvariant()}");
            DrawNode(handle, ToScreen(_branchPositions[(int) branch]), Scaled(BranchRadius), color.WithAlpha(0.25f), color, title, true, false, NCPharmaNodeState.Confirmed);
        }

        foreach (var node in _nodes)
        {
            var color = BranchColor(node.Branch);
            var fill = node.Chosen ? color : Color.FromHex("#1B272C");
            var outline = node.Locked ? UnknownColor.WithAlpha(0.6f) : node.Knowledge switch
            {
                NCPharmaNodeState.Unknown => UnknownColor,
                NCPharmaNodeState.Suspected => SuspectedColor,
                _ => color,
            };
            var radius = Scaled(NodeRadius) * (_hover == node ? 1.12f : 1f);
            DrawNode(handle, ToScreen(node.World), radius, fill, outline, node.Title, false, node.Locked, node.Knowledge);
        }
    }

    private void DrawNode(
        DrawingHandleScreen handle,
        Vector2 position,
        float radius,
        Color fill,
        Color outline,
        string title,
        bool emphasised,
        bool locked,
        NCPharmaNodeState knowledge)
    {
        handle.DrawCircle(position, radius, fill);
        handle.DrawCircle(position, radius, outline, false);
        handle.DrawCircle(position, radius - 1f, outline, false);

        if (!emphasised && knowledge == NCPharmaNodeState.Unknown)
            DrawCentered(handle, _bold, position, "?", outline, 1.4f);
        else if (locked)
            DrawCentered(handle, _bold, position, "×", outline, 1.4f);

        var font = emphasised ? _bold : _font;
        var color = locked ? UnknownColor : Color.White;
        var scale = _zoom * UIScale;
        var y = position.Y + radius + 3f * UIScale;
        foreach (var line in Wrap(title))
        {
            handle.DrawString(font, new Vector2(position.X - TextWidth(font, line, scale) / 2f, y), line, scale, color);
            y += font.GetLineHeight(scale);
        }
    }

    private void DrawCentered(DrawingHandleScreen handle, Font font, Vector2 position, string text, Color color, float scale)
    {
        var drawScale = scale * _zoom * UIScale;
        var size = new Vector2(TextWidth(font, text, drawScale), font.GetHeight(drawScale));
        handle.DrawString(font, position - size / 2f, text, drawScale, color);
    }

    private static float TextWidth(Font font, string text, float scale)
    {
        var width = 0f;
        foreach (var rune in text.EnumerateRunes())
            width += font.GetCharMetrics(rune, scale)?.Advance ?? 0;
        return width;
    }

    /// <summary>Breaks a title into at most three short lines so a long name is never cut off.</summary>
    private static IEnumerable<string> Wrap(string title)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > LabelWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : current + " " + word;
            }
        }

        if (current.Length > 0)
            lines.Add(current);
        if (lines.Count > 3)
        {
            lines = lines.Take(3).ToList();
            lines[2] = lines[2].Length > LabelWidth - 1 ? lines[2][..(LabelWidth - 1)] + "…" : lines[2] + "…";
        }

        return lines;
    }

    private NCPharmaTreeNode? Pick(Vector2 pixel)
    {
        NCPharmaTreeNode? best = null;
        var bestDistance = float.MaxValue;
        foreach (var node in _nodes)
        {
            var distance = Vector2.Distance(ToScreen(node.World), pixel);
            if (distance <= Scaled(NodeRadius) && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_panning)
        {
            _pan += args.Relative * UIScale;
            _moved = true;
            args.Handle();
            return;
        }

        var hit = Pick(args.RelativePixelPosition);
        if (hit != _hover)
        {
            _hover = hit;
            NodeHovered?.Invoke(hit?.Id);
        }
    }

    protected override void MouseExited()
    {
        base.MouseExited();
        if (_hover == null)
            return;

        _hover = null;
        NodeHovered?.Invoke(null);
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        var before = (args.RelativePixelPosition - Center) / (_zoom * UIScale);
        _zoom = Math.Clamp(_zoom * (1f + args.Delta.Y * 0.12f), 0.3f, 1.8f);
        _pan = args.RelativePixelPosition - PixelSize / 2f - before * _zoom * UIScale;
        args.Handle();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.Use && args.Function != EngineKeyFunctions.UIClick &&
            args.Function != EngineKeyFunctions.UseSecondary)
        {
            return;
        }

        _panning = true;
        _moved = false;
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (!_panning)
            return;

        _panning = false;
        if (_moved || args.Function == EngineKeyFunctions.UseSecondary)
            return;

        var hit = Pick(args.RelativePixelPosition);
        if (hit != null && !hit.Locked)
        {
            NodePressed?.Invoke(hit.Id);
        }
        else if (hit == null && Vector2.Distance(ToScreen(Vector2.Zero), args.RelativePixelPosition) <= Scaled(HubRadius))
        {
            HubPressed?.Invoke();
        }

        args.Handle();
    }

    protected override void ControlFocusExited()
    {
        base.ControlFocusExited();
        _panning = false;
    }
}
