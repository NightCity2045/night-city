// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using Content.Client._NC.City.Zones;
using Content.Client.Administration.Managers;
using Content.Client.Gameplay;
using Content.Client.Sandbox;
using Content.Client.UserInterface.Systems.DecalPlacer;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Editor;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Components;
using Content.Shared._NC.ZLevels.Core.Components;
using JetBrains.Annotations;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controllers.Implementations;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._NC.Mapping.UI;

/// <summary>
/// Coordinates existing mapping tools and the Night City zone editor from one admin-facing window.
/// </summary>
[UsedImplicitly]
public sealed partial class NCMappingHubUIController : UIController, IOnStateChanged<GameplayState>
{
    [Dependency] private IClientAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    [UISystemDependency] private readonly SandboxSystem _sandbox = default!;
    [UISystemDependency] private readonly NCMappingHubClientSystem _mapping = default!;
    [UISystemDependency] private readonly NCZoneEditorClientSystem _zones = default!;

    private readonly List<int> _availableDepths = [];
    private NCMappingHubWindow? _window;
    private int? _selectedDepth;
    private string? _selectedKind;
    private NCZoneId _selectedParent;
    private int _selectedGeometry = -1;
    private int _selectedVertex = -1;
    private NCZoneActivityMode _selectedActivity = NCZoneActivityMode.Active;
    private int _brushSize = 1;
    private PendingTileBrush? _pendingTileBrush;

    private EntitySpawningUIController EntitySpawningController =>
        UIManager.GetUIController<EntitySpawningUIController>();

    private TileSpawningUIController TileSpawningController =>
        UIManager.GetUIController<TileSpawningUIController>();

    private DecalPlacerUIController DecalPlacerController =>
        UIManager.GetUIController<DecalPlacerUIController>();

    public void OnStateEntered(GameplayState state)
    {
        EnsureWindow();
        _mapping.FloorChangeCompleted += OnFloorChangeCompleted;
        _zones.SnapshotUpdated += RefreshZones;
        _zones.OperationCompleted += OnZoneOperationCompleted;
        _zones.ToolChanged += RefreshToolStatus;
        _zones.ValidationUpdated += RefreshValidation;
    }

    public void OnStateExited(GameplayState state)
    {
        _mapping.FloorChangeCompleted -= OnFloorChangeCompleted;
        _zones.SnapshotUpdated -= RefreshZones;
        _zones.OperationCompleted -= OnZoneOperationCompleted;
        _zones.ToolChanged -= RefreshToolStatus;
        _zones.ValidationUpdated -= RefreshValidation;
        _zones.CancelTool();
        _window?.Close();
        _window = null;
        _availableDepths.Clear();
        _selectedDepth = null;
    }

    public void ToggleWindow()
    {
        if (!_admins.CanAdminPlace())
            return;

        EnsureWindow();
        if (_window!.IsOpen)
        {
            _zones.CancelTool();
            _window.Close();
            return;
        }

        UIManager.ClickSound();
        RefreshFloors();
        if (_zones.Enabled)
            _zones.Request();
        else
            _zones.Open();
        RefreshZones();
        _window.OpenCentered();
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;

        _window = UIManager.CreateWindow<NCMappingHubWindow>();
        _window.OnClose += _zones.CancelTool;
        BindToolButtons();
        BindFloorButtons();
        BindZoneButtons();
        PopulateStaticOptions();
    }

    private void BindToolButtons()
    {
        _window!.EntitiesButton.OnPressed += _ => EntitySpawningController.ToggleWindow();
        _window.TilesButton.OnPressed += _ => TileSpawningController.ToggleWindow();
        _window.DecalsButton.OnPressed += _ => DecalPlacerController.ToggleWindow();
        _window.SubfloorButton.OnPressed += _ => _sandbox.ToggleSubFloor();
        _window.MarkersButton.OnPressed += _ => _sandbox.ShowMarkers();
        _window.BoundsButton.OnPressed += _ => _sandbox.ShowBb();
        _window.LightButton.OnPressed += _ => _sandbox.ToggleLight();
        _window.FovButton.OnPressed += _ => _sandbox.ToggleFov();
        _window.ShadowsButton.OnPressed += _ => _sandbox.ToggleShadows();
        _window.ThermalButton.OnPressed += _ => _sandbox.ToggleThermalVision();
        _window.AghostButton.OnPressed += _ => _sandbox.GiveAGhost();
        _window.AccessButton.OnPressed += _ => _sandbox.GiveAdminAccess();
        _window.RespawnButton.OnPressed += _ => _sandbox.Respawn();
        _window.SuicideButton.OnPressed += _ => _sandbox.Suicide();
    }

    private void BindFloorButtons()
    {
        _window!.FloorSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedDepth = args.Button.SelectedMetadata as int?;
        };
        _window.FloorDownButton.OnPressed += _ => RequestAdjacentFloor(-1);
        _window.FloorUpButton.OnPressed += _ => RequestAdjacentFloor(1);
        _window.GoToFloorButton.OnPressed += _ => RequestSelectedFloor();
        _window.RefreshFloorsButton.OnPressed += _ => RefreshFloors();
    }

    private void BindZoneButtons()
    {
        _window!.ToggleZonesButton.OnPressed += _ => ToggleZones();
        _window.RefreshZonesButton.OnPressed += _ => _zones.Request();
        _window.DiscardDraftButton.OnPressed += _ => _zones.Request(discardDraft: true);
        _window.UndoButton.OnPressed += _ => _zones.Undo();
        _window.RedoButton.OnPressed += _ => _zones.Redo();
        _window.ValidateButton.OnPressed += _ => _zones.ValidateDraft();

        _window.ZoneSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            if (args.Button.SelectedMetadata is NCZoneId zoneId)
                _zones.Select(zoneId);
        };
        _window.ZoneSearchEdit.OnTextChanged += _ => RefreshZones();

        _window.ZoneKindSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedKind = args.Button.SelectedMetadata as string;
            PopulateParents();
        };
        _window.ZoneParentSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedParent = args.Button.SelectedMetadata is NCZoneId parent
                ? parent
                : default;
        };
        _window.ZoneActivitySelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            if (args.Button.SelectedMetadata is NCZoneActivityMode mode)
                _selectedActivity = mode;
        };
        _window.CreateZoneButton.OnPressed += _ => CreateZone();
        _window.UpdateZoneButton.OnPressed += _ => UpdateZone();
        _window.DeleteZoneButton.OnPressed += _ =>
        {
            if (_zones.SelectedZone.IsValid)
                _zones.DeleteZone(_zones.SelectedZone);
        };

        _window.GeometrySelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedGeometry = args.Button.SelectedMetadata as int? ?? -1;
            RefreshGeometryScope();
            RefreshVertices();
        };
        _window.GlobalZCheckBox.OnPressed += _ => RefreshGeometryScopeInputs();
        _window.MinZFloorSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            if (args.Button.SelectedMetadata is int depth)
                _window.MinZEdit.Text = depth.ToString(CultureInfo.InvariantCulture);
        };
        _window.MaxZFloorSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            if (args.Button.SelectedMetadata is int depth)
                _window.MaxZEdit.Text = depth.ToString(CultureInfo.InvariantCulture);
        };
        _window.MinZEdit.OnTextChanged += _ => SyncGeometryFloorSelectors();
        _window.MaxZEdit.OnTextChanged += _ => SyncGeometryFloorSelectors();
        _window.ApplyGeometryScopeButton.OnPressed += _ => ApplyGeometryScope();
        _window.DrawPolygonButton.OnPressed += _ => StartPolygon();
        _window.CreateTileMaskButton.OnPressed += _ =>
        {
            if (!TryReadInt(_window.GeometryZEdit.Text, out var z))
            {
                ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
                return;
            }

            if (!_zones.CreateTileMask(z))
                ShowZoneStatus("nc-mapping-hub-zone-select-first");
        };
        _window.DeleteGeometryButton.OnPressed += _ =>
        {
            if (_zones.SelectedZone.IsValid && _selectedGeometry >= 0)
                _zones.DeleteGeometry(_zones.SelectedZone, _selectedGeometry);
        };
        _window.FinishToolButton.OnPressed += _ =>
        {
            if (!_zones.FinishPolygon())
                _zones.CancelTool();
        };
        _window.CancelToolButton.OnPressed += _ => _zones.CancelTool();

        _window.VertexSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedVertex = args.Button.SelectedMetadata as int? ?? -1;
        };
        _window.MoveVertexButton.OnPressed += _ =>
        {
            if (!TryReadFloat(_window.SnapEdit.Text, out var snap))
            {
                ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
                RefreshToolStatus();
                return;
            }

            if (!_zones.StartMoveVertex(_selectedGeometry, _selectedVertex, snap))
            {
                ShowZoneStatus("nc-mapping-hub-zone-select-vertex-first");
                RefreshToolStatus();
            }
        };
        _window.InsertVertexButton.OnPressed += _ =>
        {
            if (!TryReadFloat(_window.SnapEdit.Text, out var snap))
            {
                ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
                RefreshToolStatus();
                return;
            }

            if (!_zones.StartInsertVertex(_selectedGeometry, _selectedVertex, snap))
            {
                ShowZoneStatus("nc-mapping-hub-zone-select-vertex-first");
                RefreshToolStatus();
            }
        };
        _window.DeleteVertexButton.OnPressed += _ =>
        {
            if (_zones.SelectedZone.IsValid && _selectedGeometry >= 0 && _selectedVertex >= 0)
                _zones.RemoveVertex(_zones.SelectedZone, _selectedGeometry, _selectedVertex);
        };

        _window.BrushSizeSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _brushSize = args.Button.SelectedMetadata as int? ?? 1;
        };
        _window.PaintTilesButton.OnPressed += _ => StartTileBrush(true);
        _window.EraseTilesButton.OnPressed += _ => StartTileBrush(false);

        _window.ExportButton.OnPressed += _ => _zones.Export(_window.ExportFileEdit.Text);
        _window.ValidationList.OnItemSelected += args =>
        {
            if (_window.ValidationList[args.ItemIndex].Metadata is not NCZoneEditorValidationError error)
                return;

            _zones.Select(error.ZoneId);
            if (error.GeometryIndex < 0)
                return;

            _selectedGeometry = error.GeometryIndex;
            SelectOptionMetadata(_window.GeometrySelector, error.GeometryIndex);
            RefreshVertices();
        };
    }

    private void PopulateStaticOptions()
    {
        if (_window == null)
            return;

        _window.ZoneKindSelector.Clear();
        var kinds = _prototypes
            .EnumeratePrototypes<NCZoneKindPrototype>()
            .OrderBy(kind => kind.HierarchyRank)
            .ToArray();
        for (var index = 0; index < kinds.Length; index++)
        {
            _window.ZoneKindSelector.AddItem(kinds[index].ID, index);
            _window.ZoneKindSelector.SetItemMetadata(index, kinds[index].ID);
        }

        if (kinds.Length != 0)
        {
            _window.ZoneKindSelector.SelectId(0);
            _selectedKind = kinds[0].ID;
        }

        _window.ZoneActivitySelector.Clear();
        foreach (var mode in Enum.GetValues<NCZoneActivityMode>())
        {
            var index = (int) mode;
            _window.ZoneActivitySelector.AddItem(mode.ToString(), index);
            _window.ZoneActivitySelector.SetItemMetadata(index, mode);
        }
        _window.ZoneActivitySelector.SelectId((int) NCZoneActivityMode.Active);

        _window.BrushSizeSelector.Clear();
        var brushes = new[] { 1, 3, 5, 7, 9 };
        for (var index = 0; index < brushes.Length; index++)
        {
            _window.BrushSizeSelector.AddItem($"{brushes[index]}x{brushes[index]}", index);
            _window.BrushSizeSelector.SetItemMetadata(index, brushes[index]);
        }
        _window.BrushSizeSelector.SelectId(0);
    }

    private void RefreshFloors()
    {
        if (_window == null)
            return;

        _availableDepths.Clear();
        _selectedDepth = null;
        _window.FloorSelector.Clear();
        PopulateGeometryFloorSelectors();

        if (_players.LocalEntity is not { } player ||
            EntityManager.GetComponent<TransformComponent>(player).MapUid is not { } map ||
            !EntityManager.TryGetComponent<NCZMapComponent>(map, out var zMap) ||
            !EntityManager.TryGetComponent<NCZMapNetworkComponent>(zMap.NetworkUid, out var network))
        {
            _window.NetworkLabel.Text = Loc.GetString("nc-mapping-hub-network-none");
            _window.CurrentFloorLabel.Text = Loc.GetString("nc-mapping-hub-floor-none");
            SetFloorButtons(false);
            return;
        }

        var networkId = EntityManager.TryGetComponent<NCZNetworkIdentityComponent>(zMap.NetworkUid, out var identity)
            ? identity.NetworkId.ToString()
            : Loc.GetString("nc-mapping-hub-network-runtime");

        _window.NetworkLabel.Text = Loc.GetString(
            "nc-mapping-hub-network",
            ("network", networkId));
        _window.CurrentFloorLabel.Text = Loc.GetString(
            "nc-mapping-hub-floor-current",
            ("floor", zMap.Depth));

        _availableDepths.AddRange(network.ZLevels
            .Where(pair => pair.Value is { } uid && uid.IsValid())
            .Select(pair => pair.Key)
            .Order());

        for (var index = 0; index < _availableDepths.Count; index++)
        {
            var depth = _availableDepths[index];
            _window.FloorSelector.AddItem(
                Loc.GetString("nc-mapping-hub-floor-option", ("floor", depth)),
                index);
            _window.FloorSelector.SetItemMetadata(index, depth);
        }
        PopulateGeometryFloorSelectors();

        var currentIndex = _availableDepths.IndexOf(zMap.Depth);
        if (currentIndex >= 0)
        {
            _window.FloorSelector.SelectId(currentIndex);
            _selectedDepth = zMap.Depth;
        }

        SetFloorButtons(_availableDepths.Count > 0);
    }

    private void SetFloorButtons(bool enabled)
    {
        if (_window == null)
            return;

        _window.FloorSelector.Disabled = !enabled;
        _window.FloorDownButton.Disabled = !enabled;
        _window.FloorUpButton.Disabled = !enabled;
        _window.GoToFloorButton.Disabled = !enabled;
    }

    private void RequestAdjacentFloor(int direction)
    {
        if (!TryGetCurrentDepth(out var current))
            return;

        var target = direction < 0
            ? _availableDepths.LastOrDefault(depth => depth < current, int.MinValue)
            : _availableDepths.FirstOrDefault(depth => depth > current, int.MaxValue);

        if (target is int.MinValue or int.MaxValue)
        {
            SetFloorStatus(Loc.GetString("nc-mapping-hub-floor-edge"));
            return;
        }

        _zones.CancelTool();
        _mapping.RequestFloorChange(target);
    }

    private void RequestSelectedFloor()
    {
        if (_selectedDepth is not { } depth)
            return;

        _zones.CancelTool();
        _mapping.RequestFloorChange(depth);
    }

    private bool TryGetCurrentDepth(out int depth)
    {
        depth = default;
        if (_players.LocalEntity is not { } player ||
            EntityManager.GetComponent<TransformComponent>(player).MapUid is not { } map ||
            !EntityManager.TryGetComponent<NCZMapComponent>(map, out var zMap))
        {
            SetFloorStatus(Loc.GetString("nc-mapping-hub-floor-none"));
            return false;
        }

        depth = zMap.Depth;
        return true;
    }

    private void OnFloorChangeCompleted(Content.Shared._NC.Mapping.NCMappingFloorChangeResult result)
    {
        SetFloorStatus(result.Message);
        RefreshFloors();
    }

    private void SetFloorStatus(string message)
    {
        if (_window != null)
            _window.FloorStatusLabel.Text = message;
    }

    private void ToggleZones()
    {
        if (_zones.Enabled)
            _zones.Close();
        else
            _zones.Open();

        RefreshZones();
    }

    private void RefreshZones()
    {
        if (_window == null)
            return;

        _window.ToggleZonesButton.Text = Loc.GetString(
            _zones.Enabled
                ? "nc-mapping-hub-zones-hide"
                : "nc-mapping-hub-zones-show");
        _window.ToggleZonesButton.Pressed = _zones.Enabled;
        _window.ZoneSelector.Clear();

        if (_zones.Snapshot is not { } snapshot)
        {
            _window.ZoneSetLabel.Text = Loc.GetString("nc-mapping-hub-zone-set-none");
            _window.DraftStatusLabel.Text = "";
            _window.ZoneDetailsLabel.Text = Loc.GetString("nc-mapping-hub-zone-none");
            _window.ZoneSelector.Disabled = true;
            RefreshZoneDetails();
            return;
        }

        TryStartPendingTileBrush(snapshot);

        _window.ZoneSetLabel.Text = Loc.GetString(
            "nc-mapping-hub-zone-set",
            ("zoneSet", snapshot.ZoneSet));
        _window.DraftStatusLabel.Text = Loc.GetString(
            snapshot.Dirty
                ? "nc-mapping-hub-zone-draft-dirty"
                : "nc-mapping-hub-zone-draft-clean",
            ("revision", snapshot.Revision));
        _window.ZoneSelector.Disabled = snapshot.Zones.Length == 0;

        var search = _window.ZoneSearchEdit.Text.Trim();
        var visibleZones = snapshot.Zones
            .Where(zone =>
                search.Length == 0 ||
                zone.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                zone.Kind.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                zone.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        for (var index = 0; index < visibleZones.Length; index++)
        {
            var zone = visibleZones[index];
            _window.ZoneSelector.AddItem($"{zone.Name} [{zone.Kind}]", index);
            _window.ZoneSelector.SetItemMetadata(index, zone.Id);
            if (zone.Id == _zones.SelectedZone)
                _window.ZoneSelector.SelectId(index);
        }

        PopulateParents();
        RefreshZoneDetails();
    }

    private void PopulateParents()
    {
        if (_window == null)
            return;

        _window.ZoneParentSelector.Clear();
        _window.ZoneParentSelector.AddItem(Loc.GetString("nc-mapping-hub-zone-parent-none"), 0);
        _window.ZoneParentSelector.SetItemMetadata(0, default(NCZoneId));

        if (_selectedKind == null ||
            !_prototypes.TryIndex<NCZoneKindPrototype>(_selectedKind, out var kind) ||
            _zones.Snapshot is not { } snapshot)
        {
            _window.ZoneParentSelector.SelectId(0);
            return;
        }

        var index = 1;
        var parentFound = false;
        foreach (var zone in snapshot.Zones
                     .Where(zone => kind.AllowedParents.Contains(zone.Kind))
                     .OrderBy(zone => zone.Name))
        {
            _window.ZoneParentSelector.AddItem($"{zone.Name} [{zone.Kind}]", index);
            _window.ZoneParentSelector.SetItemMetadata(index, zone.Id);
            if (zone.Id == _selectedParent)
            {
                _window.ZoneParentSelector.SelectId(index);
                parentFound = true;
            }
            index++;
        }

        if (!parentFound)
        {
            _selectedParent = default;
            _window.ZoneParentSelector.SelectId(0);
        }
    }

    private void RefreshZoneDetails()
    {
        if (_window == null)
            return;

        var preferredGeometry = _selectedGeometry;
        _selectedGeometry = -1;
        _selectedVertex = -1;
        _window.GeometrySelector.Clear();
        _window.VertexSelector.Clear();

        if (_zones.Snapshot is not { } snapshot ||
            snapshot.Zones.FirstOrDefault(zone => zone.Id == _zones.SelectedZone) is not { } selected)
        {
            _window.ZoneDetailsLabel.Text = Loc.GetString("nc-mapping-hub-zone-none");
            return;
        }

        _window.ZoneDetailsLabel.Text = Loc.GetString(
            "nc-mapping-hub-zone-details",
            ("id", selected.Id.ToString()),
            ("kind", selected.Kind),
            ("geometry", selected.Geometry.Length),
            ("activity", selected.ActivityMode.ToString()));
        _window.ZoneNameEdit.Text = selected.Name;
        _window.ZonePriorityEdit.Text = selected.Priority.ToString(CultureInfo.InvariantCulture);
        _selectedKind = selected.Kind;
        SelectOptionMetadata(_window.ZoneKindSelector, selected.Kind);
        _selectedParent = selected.Parent;
        PopulateParents();
        SelectOptionMetadata(_window.ZoneParentSelector, selected.Parent);
        var supportsActivity = _prototypes.TryIndex<NCZoneKindPrototype>(selected.Kind, out var kind) &&
                               kind.SupportsActivityMode;
        _window.ZoneActivitySelector.Disabled = !supportsActivity;
        _selectedActivity = supportsActivity
            ? selected.ActivityMode
            : NCZoneActivityMode.Active;
        SelectOptionMetadata(_window.ZoneActivitySelector, selected.ActivityMode);

        for (var index = 0; index < selected.Geometry.Length; index++)
        {
            var geometry = selected.Geometry[index];
            _window.GeometrySelector.AddItem(
                $"{index + 1}. {geometry.Kind} ({GeometryDepthText(geometry)})",
                index);
            _window.GeometrySelector.SetItemMetadata(index, index);
        }

        if (selected.Geometry.Length != 0)
        {
            _selectedGeometry = preferredGeometry >= 0 && preferredGeometry < selected.Geometry.Length
                ? preferredGeometry
                : 0;
            _window.GeometrySelector.SelectId(_selectedGeometry);
            RefreshGeometryScope();
            RefreshVertices();
        }
        else
        {
            RefreshGeometryScope();
        }

        if (_zones.TryGetCurrentDepth(out var currentZ))
        {
            _window.GeometryZEdit.Text = currentZ.ToString(CultureInfo.InvariantCulture);
            if (_selectedGeometry < 0)
            {
                _window.MinZEdit.Text = currentZ.ToString(CultureInfo.InvariantCulture);
                _window.MaxZEdit.Text = currentZ.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private void RefreshVertices()
    {
        if (_window == null)
            return;

        _window.VertexSelector.Clear();
        _selectedVertex = -1;
        var zone = _zones.Snapshot?.Zones.FirstOrDefault(candidate => candidate.Id == _zones.SelectedZone);
        if (zone == null ||
            _selectedGeometry < 0 ||
            _selectedGeometry >= zone.Geometry.Length)
        {
            return;
        }

        var geometry = zone.Geometry[_selectedGeometry];
        for (var index = 0; index < geometry.Vertices.Length; index++)
        {
            var vertex = geometry.Vertices[index];
            _window.VertexSelector.AddItem(
                (index + 1).ToString(CultureInfo.InvariantCulture) + ": " +
                vertex.X.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                vertex.Y.ToString("0.###", CultureInfo.InvariantCulture),
                index);
            _window.VertexSelector.SetItemMetadata(index, index);
        }

        if (geometry.Vertices.Length != 0)
        {
            _window.VertexSelector.SelectId(0);
            _selectedVertex = 0;
        }
    }

    private void CreateZone()
    {
        if (_zones.Snapshot == null)
        {
            ShowZoneStatus("nc-mapping-hub-zone-editor-not-ready");
            _zones.Open();
            return;
        }

        if (_selectedKind == null)
        {
            ShowZoneStatus("nc-mapping-hub-zone-kind-required");
            return;
        }

        var name = _window!.ZoneNameEdit.Text.Trim();
        if (name.Length == 0)
        {
            var number = _zones.Snapshot.Zones.Count(zone => zone.Kind == _selectedKind) + 1;
            name = $"{_selectedKind} {number}";
            _window.ZoneNameEdit.Text = name;
        }

        ShowZoneStatus("nc-mapping-hub-zone-request-sent");
        _zones.CreateZone(_selectedKind, name, _selectedParent);
    }

    private void UpdateZone()
    {
        if (!_zones.SelectedZone.IsValid ||
            !TryReadInt(_window!.ZonePriorityEdit.Text, out var priority))
        {
            return;
        }

        _zones.UpdateZone(
            _zones.SelectedZone,
            _window.ZoneNameEdit.Text,
            _selectedParent,
            priority,
            _selectedActivity);
    }

    private void StartPolygon()
    {
        if (_window == null ||
            !TryReadInt(_window.GeometryZEdit.Text, out var z) ||
            !TryReadInt(_window.MinZEdit.Text, out var minZ) ||
            !TryReadInt(_window.MaxZEdit.Text, out var maxZ) ||
            !TryReadFloat(_window.SnapEdit.Text, out var snap))
        {
            ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
            RefreshToolStatus();
            return;
        }

        var global = _window.GlobalZCheckBox.Pressed;
        var kind = !global && minZ != maxZ
            ? NCZoneGeometryKind.Volume
            : NCZoneGeometryKind.Polygon;
        if (!_zones.StartPolygon(
                kind,
                z,
                minZ,
                maxZ,
                snap,
                global))
        {
            ShowZoneStatus("nc-mapping-hub-zone-select-first");
            RefreshToolStatus();
        }
    }

    private void ApplyGeometryScope()
    {
        if (_window == null ||
            !_zones.SelectedZone.IsValid ||
            _selectedGeometry < 0 ||
            !TryReadInt(_window.MinZEdit.Text, out var minZ) ||
            !TryReadInt(_window.MaxZEdit.Text, out var maxZ))
        {
            ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
            return;
        }

        _zones.SetGeometryScope(
            _zones.SelectedZone,
            _selectedGeometry,
            _window.GlobalZCheckBox.Pressed,
            minZ,
            maxZ);
    }

    private void RefreshGeometryScope()
    {
        if (_window == null)
            return;

        var zone = _zones.Snapshot?.Zones.FirstOrDefault(candidate =>
            candidate.Id == _zones.SelectedZone);
        if (zone == null ||
            _selectedGeometry < 0 ||
            _selectedGeometry >= zone.Geometry.Length)
        {
            _window.GlobalZCheckBox.Pressed = false;
            _window.GlobalZCheckBox.Disabled = false;
            _window.ApplyGeometryScopeButton.Disabled = true;
            RefreshGeometryScopeInputs();
            return;
        }

        var geometry = zone.Geometry[_selectedGeometry];
        var tileMask = geometry.Kind == NCZoneGeometryKind.TileMask;
        _window.GlobalZCheckBox.Pressed = geometry.Global;
        _window.GlobalZCheckBox.Disabled = false;
        _window.ApplyGeometryScopeButton.Disabled = tileMask;

        if (!geometry.Global)
        {
            var minZ = geometry.Kind == NCZoneGeometryKind.Polygon
                ? geometry.Z
                : geometry.MinZ;
            var maxZ = geometry.Kind == NCZoneGeometryKind.Polygon
                ? geometry.Z
                : geometry.MaxZ;
            _window.MinZEdit.Text = minZ.ToString(CultureInfo.InvariantCulture);
            _window.MaxZEdit.Text = maxZ.ToString(CultureInfo.InvariantCulture);
        }

        RefreshGeometryScopeInputs();
    }

    private void RefreshGeometryScopeInputs()
    {
        if (_window == null)
            return;

        var disabled = _window.GlobalZCheckBox.Pressed;
        _window.MinZEdit.Editable = !disabled;
        _window.MaxZEdit.Editable = !disabled;
        _window.MinZFloorSelector.Disabled = disabled || _availableDepths.Count == 0;
        _window.MaxZFloorSelector.Disabled = disabled || _availableDepths.Count == 0;
        SyncGeometryFloorSelectors();
    }

    /// <summary>
    /// Loaded floors are shortcuts; manual numeric input remains available for unloaded future floors.
    /// </summary>
    private void PopulateGeometryFloorSelectors()
    {
        if (_window == null)
            return;

        _window.MinZFloorSelector.Clear();
        _window.MaxZFloorSelector.Clear();
        var manual = Loc.GetString("nc-mapping-hub-zone-manual-z");
        _window.MinZFloorSelector.AddItem(manual, 0);
        _window.MaxZFloorSelector.AddItem(manual, 0);

        for (var index = 0; index < _availableDepths.Count; index++)
        {
            var depth = _availableDepths[index];
            var label = Loc.GetString("nc-mapping-hub-floor-option", ("floor", depth));
            var id = index + 1;
            _window.MinZFloorSelector.AddItem(label, id);
            _window.MinZFloorSelector.SetItemMetadata(index + 1, depth);
            _window.MaxZFloorSelector.AddItem(label, id);
            _window.MaxZFloorSelector.SetItemMetadata(index + 1, depth);
        }

        RefreshGeometryScopeInputs();
    }

    private void SyncGeometryFloorSelectors()
    {
        if (_window == null ||
            _window.MinZFloorSelector.ItemCount == 0 ||
            _window.MaxZFloorSelector.ItemCount == 0)
        {
            return;
        }

        if (TryReadInt(_window.MinZEdit.Text, out var minZ) && _availableDepths.Contains(minZ))
            SelectOptionMetadata(_window.MinZFloorSelector, minZ);
        else
            _window.MinZFloorSelector.SelectId(0);

        if (TryReadInt(_window.MaxZEdit.Text, out var maxZ) && _availableDepths.Contains(maxZ))
            SelectOptionMetadata(_window.MaxZFloorSelector, maxZ);
        else
            _window.MaxZFloorSelector.SelectId(0);
    }

    private void StartTileBrush(bool add)
    {
        if (_zones.StartTileBrush(_selectedGeometry, _brushSize, add))
            return;

        if (_zones.Snapshot is not { } snapshot ||
            snapshot.Zones.FirstOrDefault(zone => zone.Id == _zones.SelectedZone) is not { } selected)
        {
            ShowZoneStatus("nc-mapping-hub-zone-select-first");
            RefreshToolStatus();
            return;
        }

        if (!_prototypes.TryIndex<NCZoneKindPrototype>(selected.Kind, out var kind) ||
            !kind.AllowedGeometry.Contains(NCZoneGeometryKind.TileMask))
        {
            ShowZoneStatus("nc-mapping-hub-zone-mask-not-allowed");
            RefreshToolStatus();
            return;
        }

        if (!TryReadInt(_window!.GeometryZEdit.Text, out var z))
        {
            ShowZoneStatus("nc-mapping-hub-zone-invalid-numbers");
            RefreshToolStatus();
            return;
        }

        _pendingTileBrush = new PendingTileBrush(
            selected.Id,
            selected.Geometry.Length,
            add);
        ShowZoneStatus("nc-mapping-hub-zone-creating-mask");
        _zones.CreateTileMask(z);
        RefreshToolStatus();
    }

    private void TryStartPendingTileBrush(NCZoneEditorSnapshot snapshot)
    {
        if (_pendingTileBrush is not { } pending ||
            snapshot.Zones.FirstOrDefault(zone => zone.Id == pending.ZoneId) is not { } zone ||
            zone.Geometry.Length <= pending.PreviousGeometryCount)
        {
            return;
        }

        for (var index = zone.Geometry.Length - 1; index >= pending.PreviousGeometryCount; index--)
        {
            if (zone.Geometry[index].Kind != NCZoneGeometryKind.TileMask)
                continue;

            _pendingTileBrush = null;
            _selectedGeometry = index;
            _zones.StartTileBrush(index, _brushSize, pending.Add);
            return;
        }
    }

    private void RefreshToolStatus()
    {
        if (_window is not { Disposed: false })
            return;

        _window.DrawPolygonButton.Pressed = _zones.Tool == NCZoneEditorTool.DrawPolygon;
        _window.MoveVertexButton.Pressed = _zones.Tool == NCZoneEditorTool.MoveVertex;
        _window.InsertVertexButton.Pressed = _zones.Tool == NCZoneEditorTool.InsertVertex;
        _window.PaintTilesButton.Pressed = _zones.Tool == NCZoneEditorTool.PaintTiles;
        _window.EraseTilesButton.Pressed = _zones.Tool == NCZoneEditorTool.EraseTiles;

        _window.ToolStatusLabel.Text = _zones.Tool switch
        {
            NCZoneEditorTool.None => Loc.GetString("nc-mapping-hub-zone-tool-none"),
            NCZoneEditorTool.DrawPolygon or NCZoneEditorTool.DrawVolume =>
                Loc.GetString(
                    "nc-mapping-hub-zone-tool-polygon",
                    ("vertices", _zones.PendingVertices.Count)),
            NCZoneEditorTool.MoveVertex => Loc.GetString("nc-mapping-hub-zone-tool-move"),
            NCZoneEditorTool.InsertVertex => Loc.GetString("nc-mapping-hub-zone-tool-insert"),
            NCZoneEditorTool.PaintTiles => Loc.GetString("nc-mapping-hub-zone-tool-paint"),
            NCZoneEditorTool.EraseTiles => Loc.GetString("nc-mapping-hub-zone-tool-erase"),
            _ => Loc.GetString("nc-mapping-hub-zone-tool-none"),
        };
    }

    private void OnZoneOperationCompleted(string message, bool success)
    {
        if (_window == null)
            return;

        if (!success)
            _pendingTileBrush = null;

        _window.ZoneOperationStatusLabel.Text = message;
    }

    private void ShowZoneStatus(string localeKey)
    {
        if (_window != null)
            _window.ZoneOperationStatusLabel.Text = Loc.GetString(localeKey);
    }

    private void RefreshValidation()
    {
        if (_window == null)
            return;

        _window.ValidationList.Clear();
        if (_zones.ValidationErrors.Length == 0)
        {
            _window.ValidationList.AddItem(Loc.GetString("nc-mapping-hub-zone-valid"));
            return;
        }

        foreach (var error in _zones.ValidationErrors)
        {
            var location = error.GeometryIndex < 0
                ? Loc.GetString("nc-mapping-hub-zone-validation-zone")
                : Loc.GetString(
                    "nc-mapping-hub-zone-validation-geometry",
                    ("number", error.GeometryIndex + 1));
            _window.ValidationList.AddItem(
                $"{error.ZoneId} / {location}: {error.Message}",
                metadata: error);
        }
    }

    private static string GeometryDepthText(NCZoneEditorGeometry geometry)
    {
        if (geometry.Global)
            return "Global Z";

        return geometry.Kind switch
        {
            NCZoneGeometryKind.Polygon => $"Z={geometry.Z}",
            NCZoneGeometryKind.Volume => $"Z={geometry.MinZ}..{geometry.MaxZ}",
            NCZoneGeometryKind.TileMask => $"{geometry.Chunks.Length} chunks",
            _ => string.Empty,
        };
    }

    private static void SelectOptionMetadata<T>(
        Robust.Client.UserInterface.Controls.OptionButton option,
        T value)
    {
        for (var index = 0; index < option.ItemCount; index++)
        {
            if (!Equals(option.GetItemMetadata(index), value))
                continue;

            option.SelectId(option.GetItemId(index));
            return;
        }
    }

    private static bool TryReadInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryReadFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
               float.IsFinite(result);
    }

    private readonly record struct PendingTileBrush(
        NCZoneId ZoneId,
        int PreviousGeometryCount,
        bool Add);
}
