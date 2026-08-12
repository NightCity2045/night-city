// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Combat.Targeting;

/// <summary>
/// Reusable target-doll presenter. Status RSI layers form the body, while loose Doll textures
/// are interaction overlays. The control owns no authoritative combat or medical state.
/// </summary>
public sealed partial class NCTargetingDollControl : BoxContainer
{
    public const float DefaultDisplayScale = 2f;
    public const byte MaximumStatusLevel = 8;

    private static readonly Vector2 EmptyCanvasSize = new(32f, 32f);

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;

    public event Action<NCTargetingState>? TargetingChanged;

    public ProtoId<NCAnatomyProfilePrototype>? ActiveProfile { get; private set; }
    public NCTargetingState State { get; private set; } = new(NCTargetingMode.Automatic, null);

    private readonly ButtonGroup _selectionGroup = new(isNoneSetAllowed: false);
    private readonly Button _automaticButton;
    private readonly Label _selectionLabel;
    private readonly NCTargetingCanvas _canvas;
    private readonly Dictionary<ProtoId<NCTargetZonePrototype>, NCTargetZoneButton> _zoneButtons = [];
    private readonly Dictionary<string, (TextureRect Control, NCTargetDollStatusLayer Definition)> _statusLayers = [];
    private readonly Dictionary<ProtoId<NCTargetZonePrototype>, byte> _statusLevels = [];
    private float _displayScale = DefaultDisplayScale;

    public float DisplayScale
    {
        get => _displayScale;
        set
        {
            var clamped = Math.Clamp(value, 1f, 8f);
            if (MathHelper.CloseTo(_displayScale, clamped))
                return;

            _displayScale = clamped;
            RebuildActiveProfile();
        }
    }

    public NCTargetingDollControl()
    {
        IoCManager.InjectDependencies(this);

        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 4;
        HorizontalAlignment = HAlignment.Center;

        _automaticButton = new Button
        {
            Text = Loc.GetString("nc-targeting-doll-automatic"),
            Group = _selectionGroup,
            HorizontalExpand = true,
        };
        _automaticButton.OnPressed += _ => ApplyState(
            new NCTargetingState(NCTargetingMode.Automatic, null),
            notify: true);

        _canvas = new NCTargetingCanvas
        {
            InheritChildMeasure = false,
            HorizontalAlignment = HAlignment.Center,
            RectClipContent = true,
        };

        _selectionLabel = new Label
        {
            Text = Loc.GetString("nc-targeting-doll-automatic"),
            HorizontalAlignment = HAlignment.Center,
        };

        AddChild(_automaticButton);
        AddChild(_canvas);
        AddChild(_selectionLabel);
        SetCanvasSize(EmptyCanvasSize);
    }

    public bool SetProfile(ProtoId<NCAnatomyProfilePrototype> profileId)
    {
        if (!_prototypes.TryIndex(profileId, out var profile) ||
            !_prototypes.TryIndex(profile.TargetDoll, out var doll))
        {
            ClearProfile();
            return false;
        }

        var previousState = State;
        if (ActiveProfile != profileId)
            _statusLevels.Clear();

        ActiveProfile = profileId;
        BuildProfile(profile, doll);

        if (!SetState(previousState))
            SetState(new NCTargetingState(NCTargetingMode.Automatic, null));

        return true;
    }

    public bool SetState(NCTargetingState state, bool notify = false)
    {
        if (state.Mode == NCTargetingMode.Automatic)
        {
            _automaticButton.Pressed = true;
            ApplyState(new NCTargetingState(NCTargetingMode.Automatic, null), notify);
            return true;
        }

        if (state.Mode != NCTargetingMode.SelectedZone ||
            state.SelectedZone is not { } selectedZone ||
            !_zoneButtons.TryGetValue(selectedZone, out var button))
        {
            return false;
        }

        button.Pressed = true;
        ApplyState(new NCTargetingState(NCTargetingMode.SelectedZone, selectedZone), notify);
        return true;
    }

    public void ClearProfile()
    {
        ActiveProfile = null;
        ClearCanvas();
        _zoneButtons.Clear();
        _statusLayers.Clear();
        _statusLevels.Clear();
        SetCanvasSize(EmptyCanvasSize);
        SetState(new NCTargetingState(NCTargetingMode.Automatic, null));
    }

    /// <summary>
    /// Applies one sanitized owner-visible severity. The server never exposes wound internals through this API.
    /// </summary>
    public bool SetStatusLevel(ProtoId<NCTargetZonePrototype> zone, byte level)
    {
        if (level > MaximumStatusLevel || ActiveProfile is not { } profileId ||
            !_prototypes.TryIndex(profileId, out var profile) || !profile.TargetZones.Contains(zone))
        {
            return false;
        }

        _statusLevels[zone] = level;
        var changed = false;
        foreach (var (_, layer) in _statusLayers)
        {
            if (!layer.Definition.SourceZones.Contains(zone))
                continue;

            changed |= RefreshStatusLayer(layer.Control, layer.Definition);
        }

        return changed;
    }

    public void SetStatusLevels(IReadOnlyDictionary<ProtoId<NCTargetZonePrototype>, byte> levels)
    {
        _statusLevels.Clear();
        foreach (var (zone, level) in levels)
        {
            if (level <= MaximumStatusLevel)
                _statusLevels[zone] = level;
        }

        foreach (var (_, layer) in _statusLayers)
            RefreshStatusLayer(layer.Control, layer.Definition);
    }

    private void RebuildActiveProfile()
    {
        if (ActiveProfile is not { } profileId ||
            !_prototypes.TryIndex(profileId, out var profile) ||
            !_prototypes.TryIndex(profile.TargetDoll, out var doll))
        {
            SetCanvasSize(EmptyCanvasSize);
            return;
        }

        var previousState = State;
        BuildProfile(profile, doll);
        if (!SetState(previousState))
            SetState(new NCTargetingState(NCTargetingMode.Automatic, null));
    }

    private void BuildProfile(NCAnatomyProfilePrototype profile, NCTargetDollProfilePrototype doll)
    {
        ClearCanvas();
        _zoneButtons.Clear();
        _statusLayers.Clear();
        SetCanvasSize(doll.CanvasSize);

        // Each Status RSI state is a full-canvas transparent layer; stacking them forms the base body.
        foreach (var definition in doll.StatusLayers)
        {
            if (!TryGetStatusTexture(definition, GetLayerSeverity(definition), out var texture))
                continue;

            var control = new TextureRect
            {
                Texture = texture,
                Stretch = TextureRect.StretchMode.Scale,
                SetSize = doll.CanvasSize * _displayScale,
                MouseFilter = MouseFilterMode.Ignore,
            };
            _canvas.AddChild(control);
            LayoutContainer.SetPosition(control, Vector2.Zero);
            _statusLayers.Add(definition.Id, (control, definition));
        }

        // Interaction overlays are intentionally above all medical status layers.
        foreach (var presentation in doll.Zones)
        {
            if (!profile.TargetZones.Contains(presentation.Zone) ||
                !_prototypes.TryIndex(presentation.Zone, out var zone) ||
                !_resources.TryGetResource<TextureResource>(presentation.SelectedTexture, out var selected) ||
                !_resources.TryGetResource<TextureResource>(presentation.HoverTexture, out var hover))
            {
                continue;
            }

            var button = new NCTargetZoneButton(
                presentation.Zone,
                selected.Texture,
                hover.Texture,
                presentation.SelectedRegion,
                presentation.HoverRegion,
                _displayScale)
            {
                Group = _selectionGroup,
                ToolTip = Loc.GetString(zone.DisplayName),
            };

            button.OnPressed += _ => ApplyState(
                new NCTargetingState(NCTargetingMode.SelectedZone, presentation.Zone),
                notify: true);

            _canvas.AddChild(button);
            LayoutContainer.SetPosition(button, presentation.Position * _displayScale);
            _zoneButtons.Add(presentation.Zone, button);
        }
    }

    private bool RefreshStatusLayer(TextureRect control, NCTargetDollStatusLayer definition)
    {
        if (!TryGetStatusTexture(definition, GetLayerSeverity(definition), out var texture))
            return false;

        control.Texture = texture;
        return true;
    }

    private byte GetLayerSeverity(NCTargetDollStatusLayer definition)
    {
        var severity = (byte) 0;
        foreach (var zone in definition.SourceZones)
            severity = Math.Max(severity, _statusLevels.GetValueOrDefault(zone));
        return severity;
    }

    private bool TryGetStatusTexture(
        NCTargetDollStatusLayer definition,
        byte level,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Texture? texture)
    {
        texture = null;
        if (level > MaximumStatusLevel ||
            !_resources.TryGetResource<RSIResource>(definition.Sprite, out var resource) ||
            !resource.RSI.TryGetState(new RSI.StateId($"{definition.StatePrefix}_{level}"), out var state))
        {
            return false;
        }

        texture = state.Frame0;
        return true;
    }

    private void ClearCanvas()
    {
        foreach (var button in _zoneButtons.Values)
            button.Group = null;

        _canvas.RemoveAllChildren();
    }

    private void SetCanvasSize(Vector2 virtualSize)
    {
        var size = virtualSize * _displayScale;
        _canvas.MinSize = size;
        _canvas.SetSize = size;
    }

    private void ApplyState(NCTargetingState state, bool notify)
    {
        if (State == state && !notify)
        {
            RefreshSelectionLabel();
            return;
        }

        State = state;
        RefreshSelectionLabel();

        if (notify)
            TargetingChanged?.Invoke(state);
    }

    private void RefreshSelectionLabel()
    {
        if (State.Mode == NCTargetingMode.Automatic || State.SelectedZone is not { } zoneId)
        {
            _selectionLabel.Text = Loc.GetString("nc-targeting-doll-automatic");
            return;
        }

        _selectionLabel.Text = _prototypes.TryIndex(zoneId, out var zone)
            ? Loc.GetString(zone.DisplayName)
            : Loc.GetString("nc-targeting-doll-unknown-target");
    }

    private sealed class NCTargetingCanvas : LayoutContainer
    {
        private static readonly Color BackgroundColor = Color.FromHex("#05090DE8");
        private static readonly Color BorderColor = Color.FromHex("#5D7F86A0");

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            handle.DrawRect(PixelSizeBox, BackgroundColor);
            handle.DrawRect(PixelSizeBox, BorderColor, filled: false);
        }
    }
}
