// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using Content.Client.Resources;
using Content.Client._NC.Meta.UI;
using Content.Shared._NC.Meta.UI;
using MuiNode = Content.Shared._NC.Meta.UI.NCMetaMuiNode;
using MuiControlKind = Content.Shared._NC.Meta.UI.NCMetaMuiControlKind;
using MuiValueKind = Content.Shared._NC.Meta.UI.NCMetaMuiValueKind;
using MuiAppContract = Content.Shared._NC.Meta.UI.NCMetaMuiPresentation;
using MuiViewContract = Content.Shared._NC.Meta.UI.NCMetaMuiView;
using MuiLimits = Content.Client._NC.Meta.Mui.NCMetaMuiValues;
using MuiUpdateResult = Content.Client._NC.Meta.Mui.NCMetaMuiUpdateResult;
using JsonNode = Content.Shared._NC.Meta.UI.NCMetaUiValue;
using JsonObject = Content.Shared._NC.Meta.UI.NCMetaUiObject;
using JsonArray = Content.Shared._NC.Meta.UI.NCMetaUiArray;
using JsonValue = Content.Shared._NC.Meta.UI.NCMetaUiScalar;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._NC.Meta.Mui;

/// <summary>Native renderer for validated META documents. Never loads XAML, CLR types, native styles or world state.</summary>
public sealed partial class NCMetaMuiRenderer : BoxContainer
{
    [Dependency] private IResourceCache _resources = default!;
    private readonly Dictionary<(string Alias, int Size), Font> _fonts = [];
    private readonly Dictionary<(string Alias, int Size), Stylesheet> _fontSheets = [];
    private readonly Dictionary<Control, (bool Focus, BaseButton.DrawModeEnum? Draw)> _interactive = [];
    private bool _rejected;
    private readonly Dictionary<string, (MuiNode Node, Control Control)> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?>> _inputs = new(StringComparer.Ordinal);
    private readonly MuiAppContract _contract;
    private readonly MuiViewContract _view;
    private readonly List<NCMetaMuiStyleRule> _styles;
    private readonly IReadOnlyDictionary<string, Texture> _images;
    private readonly NCMetaMuiSessionState _state;
    private readonly HashSet<Control> _hovered = [];
    private readonly Dictionary<Control, Pulse> _pulses = [];
    private float _animationAccumulator;
    public event Action<string, string>? ActionRequested;
    public event Action? ResyncRequested;
    public long Revision => _state.Revision;
    public Guid SessionId => _state.SessionId;
    public int NativeControlCount => Count(this) - 1;
    private static int Count(Control control) => 1 + control.Children.Sum(Count);

    public NCMetaMuiRenderer(NCMetaMuiPresentation contract, string viewId,
        Guid sessionId, IReadOnlyDictionary<string, Texture>? images = null)
    {
        IoCManager.InjectDependencies(this);
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        VerticalExpand = true;
        _contract = contract;
        if (!NCMetaMuiValues.PresentationValid(contract, viewId)) throw new ArgumentException("Invalid MUI presentation.");
        _view = contract.Views[viewId];
        _styles = contract.Styles;
        _state = new(sessionId, _view.Fields);
        _images = images ?? contract.Resources.Where(x => x is "images/info" or "images/home" or "images/inventory").ToDictionary(x => x,
            x => _resources.GetResource<TextureResource>(x switch
            {
                "images/info" => "/Textures/Interface/info.svg.192dpi.png", "images/home" => "/Textures/Interface/home.png",
                _ => "/Textures/Interface/inventory.svg.192dpi.png",
            }).Texture);
        AddChild(CreateNode(contract.Root));
        if (NativeControlCount > MuiLimits.Controls)
        {
            NCMetaUiLifecycle.RemoveChildren(this);
            _nodes.Clear(); _inputs.Clear(); _pulses.Clear(); _interactive.Clear();
            _rejected = true;
        }
    }

    public MuiUpdateResult TryUpdate(long baseRevision, long revision, NCMetaUiValue? values, bool snapshot)
    {
        if (_rejected) return MuiUpdateResult.Invalid;
        var result = snapshot ? _state.TryApplySnapshot(revision, values) : _state.TryApplyPatch(baseRevision, revision, values);
        if (result == MuiUpdateResult.Gap)
            ResyncRequested?.Invoke();
        if (result == MuiUpdateResult.Applied)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            if (!snapshot)
            {
                if (values is NCMetaUiObject patch)
                {
                    if (patch["set"] is NCMetaUiObject set) foreach (var field in set) changed.Add(field.Key);
                    if (patch["remove"] is NCMetaUiArray remove) foreach (var field in remove)
                        if (field is NCMetaUiScalar scalar && scalar.TryGetValue<string>(out var name)) changed.Add(name);
                }
            }
            foreach (var (node, control) in _nodes.Values)
                if (snapshot || DependsOn(node, changed)) ApplyNode(node, control);
        }
        return result;
    }

    private static bool DependsOn(MuiNode node, HashSet<string> changed) => node.Bindings.Values.Any(binding =>
        changed.Any(field => binding.Path == field || binding.Path.StartsWith(field + ".", StringComparison.Ordinal))) ||
        node.Kind == MuiControlKind.Select && node.Children.Any(child => DependsOn(child, changed));

    private Control CreateNode(MuiNode node)
    {
        Control control = node.Kind switch
        {
            MuiControlKind.Row => new BoxContainer { Orientation = LayoutOrientation.Horizontal },
            MuiControlKind.Grid => new GridContainer { Columns = (int) Number(node, "columns", 2) },
            MuiControlKind.Panel or MuiControlKind.Dialog => new PanelContainer(),
            MuiControlKind.Text or MuiControlKind.Header or MuiControlKind.Validation => new Label(),
            MuiControlKind.Button => new Button(),
            MuiControlKind.Input or MuiControlKind.NumberInput => new LineEdit(),
            MuiControlKind.Toggle => new CheckBox(),
            MuiControlKind.Select => new OptionButton(),
            MuiControlKind.Tabs => new TabContainer(),
            MuiControlKind.Scroll => new ScrollContainer(),
            MuiControlKind.List or MuiControlKind.Table => new NCMetaMuiTable(),
            MuiControlKind.Image => new TextureRect(),
            MuiControlKind.Chart or MuiControlKind.Map or MuiControlKind.Schematic => new NCMetaMuiPlot(node.Kind),
            _ => new BoxContainer { Orientation = LayoutOrientation.Vertical },
        };
        control.HorizontalExpand = true;
        control.Name = node.Id;
        _nodes.Add(node.Id, (node, control));
        _interactive[control] = (control.HasKeyboardFocus(), control is BaseButton b ? b.DrawMode : null);
        control.OnMouseEntered += _ => { _hovered.Add(control); ApplyStyle(node, control); };
        control.OnMouseExited += _ => { _hovered.Remove(control); ApplyStyle(node, control); };
        if (control is BoxContainer box)
            box.SeparationOverride = (int) Number(node, "gap", 4);
        if (control is Button button)
            button.OnPressed += _ => TryInvoke(node.Properties.GetValueOrDefault("action", FindFormAction(node)));
        if (control is NCMetaMuiTable table)
            table.Requested += (property, values) => TryInvoke(node.Properties.GetValueOrDefault(property, ""), values);
        if (control is CheckBox check)
            AddInput(node, () => check.Pressed);
        else if (control is LineEdit input)
        {
            input.PlaceHolder = node.Properties.GetValueOrDefault("placeholder", "");
            input.OnTextEntered += _ =>
            {
                var form = FindFormAction(node);
                if (form.Length > 0)
                    TryInvoke(form);
            };
            AddInput(node, () => ParseInput(node, input.Text));
        }
        else if (control is OptionButton select)
        {
            for (var i = 0; i < node.Children.Count; i++)
                select.AddItem(node.Children[i].Properties.GetValueOrDefault("text", ""), i);
            AddInput(node, () => ParseInput(node, select.SelectedId >= 0 && select.SelectedId < node.Children.Count
                ? Value(node.Children[select.SelectedId], "value") : ""));
        }
        if (control is TextureRect image && node.Properties.TryGetValue("resource", out var resource) && _images.TryGetValue(resource, out var texture))
            image.Texture = texture;
        if (node.Kind != MuiControlKind.Select)
        {
            foreach (var child in node.Children)
            {
                var childControl = CreateNode(child);
                control.AddChild(childControl);
                if (node.Kind == MuiControlKind.Tabs)
                    TabContainer.SetTabTitle(childControl, child.Properties.GetValueOrDefault("title", ""));
            }
        }
        ApplyNode(node, control);
        return control;
    }

    private string FindFormAction(MuiNode node)
    {
        // Form inputs have no code or native event names. Walk only this immutable validated tree.
        foreach (var (candidate, _) in _nodes.Values)
            if (candidate.Kind == MuiControlKind.Form && Descends(candidate, node.Id))
                return candidate.Properties.GetValueOrDefault("action", "");
        return "";
    }

    private static bool Descends(MuiNode node, string id) => node.Children.Any(x => x.Id == id || Descends(x, id));

    private void AddInput(MuiNode node, Func<object?> read)
    {
        if (node.Properties.TryGetValue("field", out var field))
            _inputs[field] = read;
    }

    private object? ParseInput(MuiNode node, string text)
    {
        if (!node.Properties.TryGetValue("field", out var field))
            return text;
        var schema = _view.Actions.Select(x => _contract.Actions[x]).Select(x => x.Inputs.GetValueOrDefault(field)).FirstOrDefault(x => x is not null);
        if (schema?.Kind == MuiValueKind.Integer)
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? integer : null;
        if (schema?.Kind == MuiValueKind.Number)
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        return text;
    }

    private void TryInvoke(string actionId, IReadOnlyDictionary<string, object?>? overrides = null)
    {
        if (_state.Closed || !_view.Actions.Contains(actionId) || !_contract.Actions.TryGetValue(actionId, out var action))
            return;
        var values = new Dictionary<string, object?>();
        foreach (var field in action.Inputs)
        {
            if (overrides is not null && overrides.TryGetValue(field.Key, out var supplied))
                values[field.Key] = supplied;
            else if (_inputs.TryGetValue(field.Key, out var input))
                values[field.Key] = input();
        }
        var data = new NCMetaUiObject();
        foreach (var pair in values) data[pair.Key] = NCMetaMuiValues.Scalar(pair.Value);
        TryRequestAction(actionId, data);
    }

    /// <summary>Uses the same declared-action and typed-input boundary as native form controls.</summary>
    public bool TryRequestAction(string actionId, NCMetaUiObject values)
    {
        if (_rejected || _state.Closed || !_view.Actions.Contains(actionId) ||
            !_contract.Actions.TryGetValue(actionId, out var action) ||
            !NCMetaMuiValues.Validate(values, action.Inputs, MuiLimits.ActionBytes) ||
            !NCMetaMuiValues.TryWrite(values, MuiLimits.ActionBytes, out var json)) return false;
        ActionRequested?.Invoke(actionId, json);
        return true;
    }

    private void ApplyNode(MuiNode node, Control control)
    {
        if (node.Kind != MuiControlKind.Tab || node.Properties.ContainsKey("visible"))
            control.Visible = Boolean(node, "visible", true);
        control.ToolTip = Value(node, "tooltip");
        control.MinWidth = (float) Number(node, "minWidth", 0);
        control.MinHeight = (float) Number(node, "minHeight", 0);
        control.MaxWidth = (float) Number(node, "maxWidth", float.PositiveInfinity);
        control.MaxHeight = (float) Number(node, "maxHeight", float.PositiveInfinity);
        if (node.Properties.ContainsKey("width"))
            control.SetWidth = (float) Number(node, "width", 0);
        if (node.Properties.ContainsKey("height"))
            control.SetHeight = (float) Number(node, "height", 0);
        control.Margin = new Thickness((float) Number(node, "margin", 0));
        if (control is Label label)
            label.Text = Value(node, "text");
        else if (control is Button button)
        {
            button.Text = Value(node, "text");
            button.Disabled = !Boolean(node, "enabled", true);
        }
        else if (control is CheckBox toggle)
        {
            toggle.Text = Value(node, "text");
            toggle.Disabled = !Boolean(node, "enabled", true);
            if (!toggle.HasKeyboardFocus())
                toggle.Pressed = Boolean(node, "value", false);
        }
        else if (control is LineEdit input)
        {
            input.Editable = Boolean(node, "enabled", true);
            if (!input.HasKeyboardFocus() && node.Properties.ContainsKey("value"))
                input.Text = Value(node, "value");
        }
        else if (control is OptionButton select)
        {
            select.Disabled = !Boolean(node, "enabled", true);
            var previousSelection = select.SelectedId;
            select.Clear();
            for (var i = 0; i < node.Children.Count; i++)
                select.AddItem(Value(node.Children[i], "text"), i);
            if (previousSelection >= 0 && previousSelection < node.Children.Count)
                select.SelectId(previousSelection);
            if (node.Properties.ContainsKey("value"))
            {
                var selected = Value(node, "value");
                for (var i = 0; i < node.Children.Count; i++)
                    if (Value(node.Children[i], "value") == selected)
                    {
                        select.SelectId(i);
                        break;
                    }
            }
        }
        else if (control is ItemList list)
        {
            list.Clear();
            if (ReadBound(node, "items") is JsonArray rows)
                foreach (var row in rows.Take(MuiLimits.PageRows))
                    list.AddItem(FormatRow(row), selectable: false);
        }
        else if (control is NCMetaMuiTable table)
        {
            var pageValue = Value(node, "page");
            var page = int.TryParse(pageValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            table.Apply(ReadBound(node, "items") as JsonArray, page, (int) Number(node, "pageSize", MuiLimits.PageRows),
                node.Properties.ContainsKey("pageAction"), node.Properties.ContainsKey("sortAction"), node.Properties.ContainsKey("filterAction"),
                node.Properties.GetValueOrDefault("rowKey", ""), node.Properties.GetValueOrDefault("rowField", ""),
                node.Properties.TryGetValue("columns", out var columns) ? columns.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null);
        }
        else if (control is NCMetaMuiPlot plot)
            plot.SetData(ReadBound(node, "items") as JsonArray, Number(node, "min", 0), Number(node, "max", 1));
        if (node.Kind == MuiControlKind.Tab)
            TabContainer.SetTabTitle(control, Value(node, "title"));
        ApplyStyle(node, control);
    }

    private void ApplyStyle(MuiNode node, Control control)
    {
        control.Modulate = Color.White;
        control.Margin = new Thickness((float) Number(node, "margin", 0));
        if (control is Label baseLabel)
        {
            baseLabel.FontColorOverride = null;
            baseLabel.FontOverride = null;
        }
        if (control is Button baseButton)
        {
            baseButton.Label.FontColorOverride = null;
            baseButton.Label.FontOverride = null;
            baseButton.StyleBoxOverride = null;
        }
        if (control is CheckBox baseCheck)
        { baseCheck.Label.FontColorOverride = null; baseCheck.Label.FontOverride = null; }
        if (control is LineEdit baseInput)
        { baseInput.StyleBoxOverride = null; baseInput.Stylesheet = null; }
        if (control is BoxContainer baseBox) baseBox.SeparationOverride = (int) Number(node, "gap", 4);
        if (control is PanelContainer basePanel)
            basePanel.PanelOverride = null;
        _pulses.Remove(control, out var previousPulse);
        var computed = new Dictionary<string, string>(StringComparer.Ordinal);
        var classes = node.Properties.GetValueOrDefault("class", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in _styles)
        {
            if (Size.X < rule.MinWidth || Size.X > rule.MaxWidth ||
                (rule.Selector[0] == '#' ? rule.Selector[1..] != node.Id : !classes.Contains(rule.Selector[1..])))
                continue;
            if (rule.State != "normal" && !(rule.State == "disabled" && !Boolean(node, "enabled", true)) &&
                !(rule.State == "focus" && control.HasKeyboardFocus()) && !(rule.State == "hover" && _hovered.Contains(control)) &&
                !(rule.State == "pressed" && control is BaseButton { DrawMode: BaseButton.DrawModeEnum.Pressed }))
                continue;
            foreach (var property in rule.Properties)
            {
                computed[property.Key] = property.Value;
                switch (property.Key)
                {
                    case "color" when control is Label label: label.FontColorOverride = Color.FromHex(property.Value); break;
                    case "color" when control is Button button: button.Label.FontColorOverride = Color.FromHex(property.Value); break;
                    case "color" when control is CheckBox check: check.Label.FontColorOverride = Color.FromHex(property.Value); break;
                    case "opacity": control.Modulate = Color.White.WithAlpha(float.Parse(property.Value, CultureInfo.InvariantCulture)); break;
                    case "margin": control.Margin = new Thickness(float.Parse(property.Value, CultureInfo.InvariantCulture)); break;
                    case "gap" when control is BoxContainer box: box.SeparationOverride = (int) float.Parse(property.Value, CultureInfo.InvariantCulture); break;
                    case "background" when control is PanelContainer panel:
                        panel.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(property.Value) };
                        break;
                    case "gradient" when control is PanelContainer gradientPanel:
                        var stops = property.Value.Split(',');
                        gradientPanel.PanelOverride = new NCMetaMuiBrush(Color.FromHex(stops[0]), Color.FromHex(stops[1]));
                        break;
                    case "animation" when _pulses.Count < MuiLimits.Animations:
                        var parts = property.Value.Split(':');
                        var duration = float.Parse(parts[1], CultureInfo.InvariantCulture) / 1000;
                        var from = float.Parse(parts[2], CultureInfo.InvariantCulture);
                        var to = float.Parse(parts[3], CultureInfo.InvariantCulture);
                        _pulses[control] = previousPulse is not null && previousPulse.Duration == duration && previousPulse.From == from && previousPulse.To == to
                            ? previousPulse : new(duration, from, to);
                        break;
                }
            }
        }
        if (computed.ContainsKey("font") || computed.ContainsKey("fontSize"))
        {
            var alias = computed.GetValueOrDefault("font", "fonts/noto-sans");
            var size = computed.TryGetValue("fontSize", out var fontSize) ? Math.Clamp((int) float.Parse(fontSize, CultureInfo.InvariantCulture), 8, 48) : 14;
            if (!_fonts.TryGetValue((alias, size), out var font))
            {
                var path = alias switch { "fonts/mono" => "/Fonts/RobotoMono/RobotoMono-Regular.ttf", "fonts/noto-bold" => "/Fonts/NotoSans/NotoSans-Bold.ttf",
                    _ => "/Fonts/NotoSans/NotoSans-Regular.ttf" };
                _fonts.Add((alias, size), font = _resources.GetFont(new[] { path, "/Fonts/NotoEmoji.ttf", "/Fonts/NotoSans/NotoSansSymbols2-Regular.ttf" }, size));
            }
            if (control is Label text) text.FontOverride = font;
            else if (control is Button textButton) textButton.Label.FontOverride = font;
            else if (control is CheckBox textCheck) textCheck.Label.FontOverride = font;
            else if (control is LineEdit textInput)
            {
                if (!_fontSheets.TryGetValue((alias, size), out var sheet))
                {
                    StyleRule rule = StylesheetHelpers.Element<LineEdit>().Identifier("nc-meta-input").Prop("font", font);
                    sheet = new Stylesheet((UserInterfaceManager.Stylesheet?.Rules ?? Array.Empty<StyleRule>()).Concat([rule]).ToArray());
                    if (_fontSheets.Count < 128) _fontSheets[(alias, size)] = sheet;
                }
                textInput.StyleIdentifier = "nc-meta-input";
                textInput.Stylesheet = sheet;
            }
        }
        if (control is TextureRect texture && computed.TryGetValue("image", out var image) && _images.TryGetValue(image, out var bitmap))
            texture.Texture = bitmap;
        if (computed.Keys.Any(x => x is "padding" or "background" or "gradient" or "borderColor" or "borderWidth" or "cornerRadius"))
        {
            var fill = computed.TryGetValue("background", out var background) ? Color.FromHex(background) : Color.Transparent;
            var first = fill;
            var last = fill;
            if (computed.TryGetValue("gradient", out var gradient))
            { var colors = gradient.Split(','); first = Color.FromHex(colors[0]); last = Color.FromHex(colors[1]); }
            var border = computed.TryGetValue("borderColor", out var borderColor) ? Color.FromHex(borderColor) : Color.Transparent;
            float Metric(string name) => computed.TryGetValue(name, out var value) ? float.Parse(value, CultureInfo.InvariantCulture) : 0;
            var brush = new NCMetaMuiBrush(first, last, border, Metric("borderWidth"), Metric("cornerRadius"));
            brush.SetContentMarginOverride(StyleBox.Margin.All, Metric("padding") + Metric("borderWidth"));
            if (control is PanelContainer panel) panel.PanelOverride = brush;
            else if (control is Button styledButton) styledButton.StyleBoxOverride = brush;
            else if (control is LineEdit styledInput) styledInput.StyleBoxOverride = brush;
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _animationAccumulator += args.DeltaSeconds;
        if (_animationAccumulator < 1f / 30)
            return;
        var delta = Math.Min(_animationAccumulator, 0.1f);
        _animationAccumulator = 0;
        foreach (var (node, control) in _nodes.Values)
        {
            var next = (control.HasKeyboardFocus(), control is BaseButton b ? (BaseButton.DrawModeEnum?) b.DrawMode : null);
            if (_interactive.TryGetValue(control, out var previous) && previous == next) continue;
            _interactive[control] = next;
            ApplyStyle(node, control);
        }
        foreach (var (control, pulse) in _pulses)
        {
            if (!control.VisibleInTree)
                continue;
            pulse.Elapsed = (pulse.Elapsed + delta) % pulse.Duration;
            var progress = (1 - MathF.Cos(2 * MathF.PI * pulse.Elapsed / pulse.Duration)) / 2;
            control.Modulate = Color.White.WithAlpha(pulse.From + (pulse.To - pulse.From) * progress);
        }
    }

    protected override void Resized()
    {
        base.Resized();
        foreach (var (node, control) in _nodes.Values)
            ApplyStyle(node, control);
    }

    private JsonNode? ReadBound(MuiNode node, string property) => node.Bindings.TryGetValue(property, out var binding) ? _state.Read(binding.Path) : null;
    private string Value(MuiNode node, string property)
    {
        if (!node.Bindings.TryGetValue(property, out var binding))
            return node.Properties.GetValueOrDefault(property, "");
        var value = _state.Read(binding.Path);
        var text = value is JsonValue j && j.TryGetValue<string>(out var str) ? str : value?.ToJsonString() ?? "";
        if (binding.Format == "upper")
            return text.ToUpperInvariant();
        if (binding.Format == "lower")
            return text.ToLowerInvariant();
        if (binding.Format is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return binding.Format == "percent" ? number.ToString("P0", CultureInfo.CurrentCulture) : number.ToString(binding.Format switch
            {
                "number:0" => "F0", "number:1" => "F1", "number:2" => "F2", _ => "G",
            }, CultureInfo.CurrentCulture);
        return text;
    }

    private bool Boolean(MuiNode node, string property, bool fallback)
    {
        if (node.Bindings.TryGetValue(property, out var binding))
        {
            var value = _state.Read(binding.Path);
            return value is JsonValue j && j.TryGetValue<bool>(out var result) ? binding.Negated != result : fallback;
        }
        return node.Properties.TryGetValue(property, out var text) && bool.TryParse(text, out var parsed) ? parsed : fallback;
    }

    private static double Number(MuiNode node, string property, double fallback) =>
        node.Properties.TryGetValue(property, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string FormatRow(JsonNode? row) => row is JsonObject obj
        ? string.Join("  |  ", obj.Take(16).Select(x => x.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : x.Value?.ToJsonString() ?? ""))
        : row is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str : row?.ToJsonString() ?? "";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NCMetaUiLifecycle.BeforeRemove(this);
            _state.Dispose();
            _inputs.Clear();
            _nodes.Clear();
            _hovered.Clear();
            _pulses.Clear();
            _fonts.Clear();
            _fontSheets.Clear();
            _interactive.Clear();
            ActionRequested = null;
            ResyncRequested = null;
        }
        base.Dispose(disposing);
    }

    private sealed class Pulse(float duration, float from, float to)
    {
        public readonly float Duration = duration;
        public readonly float From = from;
        public readonly float To = to;
        public float Elapsed;
    }
}
