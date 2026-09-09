// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Input;

namespace Content.Client._NC.Vehicle.Workshop;

/// <summary>Composes actual parts in one coordinate space. Zoom/pan never change the submitted contact point.</summary>
public sealed partial class NCVehicleWorkshopField : Control
{
    [Dependency] private IResourceCache _resources = default!;
    private NCVehicleWorkshopPrototype? _profile;
    private NCVehicleChassisPrototype? _chassis;
    private readonly Dictionary<string, Texture> _textures = new();
    private Dictionary<string, NCVehicleWorkshopPartState> _parts = new();
    private Dictionary<NCVehicleFluidCircuit, NCVehicleWorkshopFluidState> _fluids = new();
    private KeyValuePair<string, NCVehicleWorkshopNode>[] _nodes = [];
    private Texture? _fastener;
    private Texture? _connector;
    private string _view = string.Empty;
    private string? _hover;
    private string? _selected;
    private string _filterKey = string.Empty;
    private Vector2 _focusOrigin;
    private Vector2 _focusSize = Vector2.One;
    private float _zoom = 1;
    private Vector2 _pan;
    private bool _panning;
    private bool _locked;
    public bool Pending { get; set; }
    public event Action<string, string, Vector2, uint>? Contact;
    public event Action<string>? Selection;

    public NCVehicleWorkshopField()
    {
        IoCManager.InjectDependencies(this);
        MinSize = new Vector2(400, 340);
        HorizontalExpand = true;
        VerticalExpand = true;
        RectClipContent = true;
        MouseFilter = MouseFilterMode.Stop;
    }

    public void SetState(NCVehicleWorkshopPrototype profile, NCVehicleChassisPrototype chassis, NCVehicleWorkshopSnapshot state, string view)
        => SetState(profile, chassis, state.Parts, state.Fluids, state.Hood, state.Lifted, state.Pending, view, null);

    /// <summary>Focuses a detached carrier and its children while retaining the profile's canonical hit coordinates.</summary>
    public void SetDetachedState(NCVehicleWorkshopPrototype profile, NCVehicleChassisPrototype chassis,
        NCVehicleWorkshopPartState[] parts, string view, IReadOnlySet<string> visibleSlots)
        => SetState(profile, chassis, parts, [], true, true, false, view, visibleSlots);

    private void SetState(NCVehicleWorkshopPrototype profile, NCVehicleChassisPrototype chassis,
        NCVehicleWorkshopPartState[] parts, NCVehicleWorkshopFluidState[] fluids, bool hood, bool lifted,
        bool pending, string view, IReadOnlySet<string>? visibleSlots)
    {
        var profileChanged = _profile != profile;
        if (profileChanged)
        {
            _profile = profile;
            _textures.Clear();
            foreach (var (id, node) in profile.Nodes)
                if (_resources.TryGetResource<TextureResource>(node.Texture, out var resource))
                    _textures[id] = resource.Texture;
            if (_resources.TryGetResource<TextureResource>(profile.FastenerTexture, out var bolt)) _fastener = bolt.Texture;
            if (_resources.TryGetResource<TextureResource>(profile.ConnectorTexture, out var connector)) _connector = connector.Texture;
        }
        var filterKey = visibleSlots == null ? string.Empty : string.Join('\n', visibleSlots.OrderBy(x => x, StringComparer.Ordinal));
        if (profileChanged || _view != view || _chassis != chassis || _filterKey != filterKey)
        {
            _view = view;
            _filterKey = filterKey;
            _zoom = 1;
            _pan = Vector2.Zero;
            _selected = null;
            _nodes = profile.Nodes.Where(p => p.Value.View == view &&
                    (visibleSlots == null ? !p.Value.DetachedOnly : visibleSlots.Contains(p.Value.Slot)))
                .OrderBy(p => p.Value.Layer).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
            SetFocus(visibleSlots != null);
        }
        _chassis = chassis;
        _parts = parts.ToDictionary(p => p.Slot);
        _fluids = fluids.ToDictionary(p => p.Circuit);
        var definition = profile.Views[view];
        _locked = definition.RequiresHood && !hood || definition.RequiresLift && !lifted;
        Pending = pending;
    }

    private void SetFocus(bool focused)
    {
        _focusOrigin = Vector2.Zero;
        _focusSize = Vector2.One;
        if (!focused || _nodes.Length == 0)
            return;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (_, node) in _nodes)
        {
            min = Vector2.Min(min, node.Position);
            max = Vector2.Max(max, node.Position + node.Size);
        }
        var side = Math.Clamp(Math.Max(max.X - min.X, max.Y - min.Y) + 0.12f, 0.2f, 1f);
        var center = (min + max) / 2;
        _focusSize = new Vector2(side);
        _focusOrigin = Vector2.Clamp(center - _focusSize / 2, Vector2.Zero, Vector2.One - _focusSize);
    }

    private bool Installed(string slot) => _parts.TryGetValue(slot, out var part) && part.Installed;
    private bool Exposed(NCVehicleWorkshopNode node) => _chassis != null &&
        NCVehicleWorkshopGeometry.Exposed(_chassis.Slots[node.Slot], Installed);

    private UIBox2 Canvas()
    {
        var size = Math.Max(1, Math.Min(PixelWidth, PixelHeight) - 20) * _zoom;
        return UIBox2.FromDimensions((PixelSize - new Vector2(size)) / 2 + _pan, new Vector2(size));
    }

    private UIBox2 NodeBox(NCVehicleWorkshopNode node)
    {
        var canvas = Canvas();
        return UIBox2.FromDimensions(canvas.TopLeft + (node.Position - _focusOrigin) / _focusSize * canvas.Size,
            node.Size / _focusSize * canvas.Size);
    }

    private (string Node, string Point)? Pick(Vector2 pixel, out Vector2 normalized)
    {
        var canvas = Canvas();
        normalized = _focusOrigin + (pixel - canvas.TopLeft) / canvas.Size * _focusSize;
        if (_locked || _chassis == null)
            return null;
        // Points and sprites share the server's canonical geometry and draw order.
        for (var i = _nodes.Length - 1; i >= 0; i--)
        {
            var (id, node) = _nodes[i];
            if (!NCVehicleWorkshopGeometry.Contains(node, normalized))
                continue;
            if (Installed(node.Slot))
            {
                var local = (normalized - node.Position) / node.Size;
                foreach (var (pointId, point) in _chassis.Slots[node.Slot].WorkPoints)
                    if (Vector2.DistanceSquared(local, point.Position) <= point.Radius * point.Radius)
                        return (id, pointId);
                return (id, string.Empty);
            }
        }
        // Empty sockets are targets only where no installed foreground part obscures the cursor.
        for (var i = _nodes.Length - 1; i >= 0; i--)
            if (Exposed(_nodes[i].Value) && NCVehicleWorkshopGeometry.Contains(_nodes[i].Value, normalized))
                return (_nodes[i].Key, string.Empty);
        return null;
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_panning)
        {
            _pan += args.Relative * UIScale;
            args.Handle();
            return;
        }
        var hit = Pick(args.RelativePixelPosition, out _);
        _hover = hit?.Node;
        ToolTip = hit is { } target && _chassis != null && _profile != null
            ? Loc.GetString(target.Point.Length == 0 ? _chassis.Slots[_profile.Nodes[target.Node].Slot].Name :
                _chassis.Slots[_profile.Nodes[target.Node].Slot].WorkPoints[target.Point].Name) : null;
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        var before = Canvas();
        var at = (args.RelativePixelPosition - before.TopLeft) / before.Size;
        _zoom = Math.Clamp(_zoom + args.Delta.Y * 0.2f, 1, 3);
        var after = Canvas();
        _pan += args.RelativePixelPosition - (after.TopLeft + at * after.Size);
        if (_zoom == 1) _pan = Vector2.Zero;
        args.Handle();
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function == EngineKeyFunctions.UseSecondary)
        {
            _panning = true;
            args.Handle();
            return;
        }
        if (Pending || args.Function != EngineKeyFunctions.Use && args.Function != EngineKeyFunctions.UIClick)
            return;
        var hit = Pick(args.RelativePixelPosition, out var position);
        if (hit is not { } target || _profile == null)
            return;
        _selected = target.Node;
        Selection?.Invoke(_profile.Nodes[target.Node].Slot);
        var state = _parts[_profile.Nodes[target.Node].Slot];
        Contact?.Invoke(target.Node, target.Point, position, state.Revision);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function == EngineKeyFunctions.UseSecondary) _panning = false;
    }

    protected override void ControlFocusExited()
    {
        base.ControlFocusExited();
        _panning = false;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromHex("#10191D"));
        var canvas = Canvas();
        // Visual guides carry no gameplay geometry; all picking uses authored node/point definitions.
        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10f;
            handle.DrawLine(canvas.TopLeft + new Vector2(canvas.Width * t, 0), canvas.BottomLeft + new Vector2(canvas.Width * t, 0), Color.FromHex("#24343A"));
            handle.DrawLine(canvas.TopLeft + new Vector2(0, canvas.Height * t), canvas.TopRight + new Vector2(0, canvas.Height * t), Color.FromHex("#24343A"));
        }
        if (_chassis == null || _locked)
            return;
        foreach (var (id, node) in _nodes)
        {
            if (!_textures.TryGetValue(id, out var texture) || !_parts.TryGetValue(node.Slot, out var part) ||
                !part.Installed && !Exposed(node))
                continue;
            var box = NodeBox(node);
            var color = part.Installed ? Color.White : Color.FromHex("#63849340");
            handle.DrawTextureRect(texture, box, color);
            if (id == _hover || id == _selected)
                handle.DrawRect(box, part.Busy ? Color.FromHex("#EAA43B") : Color.FromHex("#72D9C5"), filled: false);
            if (!part.Installed)
                continue;
            if (node.Fluid is { } circuit && _fluids.TryGetValue(circuit, out var fluid))
            {
                var gauge = UIBox2.FromDimensions(box.TopLeft + new Vector2(box.Width - 8, 2), new Vector2(6, box.Height - 4));
                handle.DrawRect(gauge, Color.FromHex("#17272D"));
                var height = gauge.Height * Math.Clamp(fluid.Fraction, 0, 1);
                var fill = UIBox2.FromDimensions(new Vector2(gauge.Left, gauge.Bottom - height), new Vector2(gauge.Width, height));
                handle.DrawRect(fill, fluid.Purity < 0.8f ? Color.FromHex("#D7804B") : Color.FromHex("#58C7B0"));
                handle.DrawRect(gauge, Color.FromHex("#D5E0D8"), filled: false);
            }
            foreach (var (pointId, point) in _chassis.Slots[node.Slot].WorkPoints)
            {
                var center = box.TopLeft + point.Position * box.Size;
                // The generous click area is independent from the physical fastener's drawn size.
                var radius = point.Radius * Math.Min(box.Width, box.Height) * 0.45f;
                var attached = part.Points.GetValueOrDefault(pointId);
                var icon = point.Kind == NCVehicleWorkshopPointKind.Fastener ? _fastener : _connector;
                if (attached && icon != null)
                    handle.DrawTextureRect(icon, UIBox2.FromDimensions(center - new Vector2(radius), new Vector2(radius * 2)));
                else
                    handle.DrawCircle(center, radius, Color.FromHex("#EEB958"), filled: false);
                var targetTorque = point.TargetTorque > 0 ? point.TargetTorque : _chassis.Slots[node.Slot].FastenerTorque;
                if (attached && point.Kind == NCVehicleWorkshopPointKind.Fastener && targetTorque > 0)
                {
                    var applied = part.PointTorque.GetValueOrDefault(pointId);
                    var correct = MathF.Abs(applied - targetTorque) <= targetTorque * point.TorqueTolerance;
                    handle.DrawCircle(center, radius * 1.35f, correct ? Color.FromHex("#58C7B0") : Color.FromHex("#E7654E"), filled: false);
                }
            }
        }
    }
}
