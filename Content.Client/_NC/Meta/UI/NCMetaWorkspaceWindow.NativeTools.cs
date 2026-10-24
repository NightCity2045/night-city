// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text.Json;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private bool _nativeToolsMode;
    private string _nativeToolsView = "";
    private Guid _toolsRelease;
    private string _toolsDevice = "";
    private string _toolsSlot = "";
    private readonly Dictionary<string, string> _toolsBindings = [];
    private readonly List<NCMetaDeviceSummary> _toolsLinkDevices = [];

    private void InitializeNativeTools()
    {
        TabContainer.SetTabTitle(NativeToolsPanel, Loc.GetString("nc-meta-tools-title"));
        TabContainer.SetTabVisible(NativeToolsPanel, false);
        ToolsInstaller.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "installer");
        ToolsRecovery.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "recovery");
        ToolsHardware.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "hardware");
        ToolsDiagnostics.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "diagnostics");
        ToolsMarket.OnPressed += _ => MainContentTabs.CurrentTab = 1;
        ToolsPermissions.OnPressed += _ => MainContentTabs.CurrentTab = 2;
        ToolsStock.OnItemSelected += args => ToolsStock.SelectId(args.Id);
        ToolsPrepare.OnPressed += _ =>
        {
            if (ToolsStock.SelectedId >= 0 && ToolsStock.SelectedId < Math.Min(32, _state.Templates.Count))
                Request(NCMetaWorkspaceOperation.PrepareStockApplication, _state.Templates[ToolsStock.SelectedId].Id);
        };
        ToolsLinks.OnItemSelected += args =>
        {
            if (_applying || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, _state.Links.Count)) return;
            _toolsSlot = _state.Links[args.ItemIndex].Slot;
            UpdateToolsLinkDevices();
        };
        ToolsLinkDevice.OnItemSelected += args =>
        {
            if (_applying || _toolsSlot.Length == 0 || args.Id < 0 || args.Id > _toolsLinkDevices.Count) return;
            ToolsLinkDevice.SelectId(args.Id);
            if (args.Id == 0) _toolsBindings.Remove(_toolsSlot);
            else _toolsBindings[_toolsSlot] = _toolsLinkDevices[args.Id - 1].DeviceId;
            ToolsBindingConsent.Pressed = false;
            UpdateToolsLinks();
        };
        ToolsBindingConsent.OnToggled += _ => UpdateToolsInstall();
        ToolsInstall.OnPressed += _ =>
        {
            if (!ToolsBindingConsent.Pressed || _state.ActiveReleaseId == Guid.Empty ||
                _state.Links.Take(64).Any(link => !_toolsBindings.ContainsKey(link.Slot))) return;
            var bindings = _state.Links.Take(64).Select(link => new NCMetaSoftwareBinding(link.Slot,
                _toolsBindings[link.Slot], link.ContractHash, link.RequiredScopes.ToList())).ToArray();
            Request(NCMetaWorkspaceOperation.Install, JsonSerializer.Serialize(bindings));
        };
    }

    private void ApplyNativeTools()
    {
        var tools = _state.NativeTools;
        var mode = tools != null;
        var change = mode != _nativeToolsMode || tools?.View != _nativeToolsView;
        if (mode && !_nativeToolsMode)
        {
            NCMetaUiLifecycle.BeforeRemove(Editor);
            _buffers.Clear(); _downloads.Clear(); _uploads.Clear();
            foreach (var tab in _fileTabButtons.Values) { NCMetaUiLifecycle.BeforeRemove(tab); tab.Dispose(); }
            _fileTabButtons.Clear();
            _languageNavigation.Clear(); _selectedFile = _savedFile = _saveRequest = Guid.Empty;
            _savedSource = _savedHash = ""; _pendingSave = null; _afterSave = null;
            Editor.TrySetSource(""); Editor.Editable = false;
            FileInfo.Text = "";
            if (SoftwarePanels.CurrentTab is 1 or 2) SoftwarePanels.CurrentTab = 0;
            ClearSoftwareSource(); Conflict.Visible = false;
        }
        _nativeToolsMode = mode; _nativeToolsView = tools?.View ?? "";
        Title = Loc.GetString(mode ? "nc-meta-tools-title" : "nc-meta-workspace-title");
        NativeToolsMenu.Visible = mode; ProjectToolbar.Visible = !mode; ReleaseInfo.Visible = !mode;
        Save.Visible = Build.Visible = Tests.Visible = Install.Visible = ExportMedia.Visible = ImportMedia.Visible = !mode;
        TabContainer.SetTabVisible(EditorTab, !mode);
        TabContainer.SetTabVisible(DevelopmentTab, !mode);
        TabContainer.SetTabVisible(SoftwarePublishPanel, !mode);
        TabContainer.SetTabVisible(SoftwareSourcePanel, !mode);
        TabContainer.SetTabVisible(NativeToolsPanel, mode);
        if (tools == null)
        {
            if (change) MainContentTabs.CurrentTab = 0;
            return;
        }
        Editor.Editable = false;
        if (change) MainContentTabs.CurrentTab = MainContentTabs.Children.ToList().IndexOf(NativeToolsPanel);
        ToolsHelp.SetMessage(Loc.GetString(tools.View == "recovery" ? "nc-meta-tools-recovery-help" :
            tools.View == "diagnostics" ? "nc-meta-tools-diagnostics-help" : "nc-meta-tools-description"));
        ToolsStockPanel.Visible = tools.View == "installer";
        ToolsMeasurements.Visible = tools.View is "hardware" or "diagnostics";
        ToolsLogs.Visible = tools.View is "diagnostics" or "recovery";
        if (_toolsRelease != _state.ActiveReleaseId || _toolsDevice != _selectedDeviceId)
        {
            _toolsRelease = _state.ActiveReleaseId; _toolsDevice = _selectedDeviceId;
            _toolsBindings.Clear(); _toolsSlot = ""; ToolsBindingConsent.Pressed = false;
        }
        var selectedStock = ToolsStock.SelectedId;
        ToolsStock.Clear();
        for (var i = 0; i < Math.Min(32, _state.Templates.Count); i++)
            ToolsStock.AddItem(_localization.DefaultCulture?.TwoLetterISOLanguageName == "ru" ? _state.Templates[i].NameRu : _state.Templates[i].Name, i);
        if (ToolsStock.ItemCount > 0) ToolsStock.SelectId(Math.Clamp(selectedStock, 0, ToolsStock.ItemCount - 1));
        ToolsPrepare.Disabled = ToolsStock.ItemCount == 0;
        UpdateToolsLinks();
        ToolsMeasurements.Clear();
        foreach (var row in tools.Hardware.Take(64))
        {
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-hardware-row", ("name", row.Name),
                ("power", Loc.GetString(row.PowerKey)), ("health", Loc.GetString(row.HealthKey))), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-processes", ("used", row.Processes), ("limit", row.MaximumProcesses)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-memory", ("used", row.ReservedMemoryBytes), ("limit", row.MemoryBytes)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-gas", ("used", row.WindowGas), ("limit", row.GasPerWindow)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-load", ("load", row.LoadFactor), ("throttle", row.ThrottlePermille)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-power", ("watts", row.AppliedWatts)), selectable: false);
            ToolsMeasurements.AddItem(row.TemperatureKelvin is { } kelvin ? Loc.GetString("nc-meta-tools-temperature", ("kelvin", kelvin)) :
                Loc.GetString("nc-meta-tools-temperature-unavailable"), selectable: false);
        }
        if (tools.Hardware.Count == 0) ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-no-hosts"), selectable: false);
        ToolsLogs.Clear(); foreach (var log in _state.Logs.TakeLast(256)) ToolsLogs.AddItem(log, selectable: false);
    }

    private void UpdateToolsLinks()
    {
        ToolsLinks.Clear();
        foreach (var link in _state.Links.Take(64))
        {
            var device = _toolsBindings.TryGetValue(link.Slot, out var id) ? _state.Devices.FirstOrDefault(row => row.DeviceId == id) : null;
            var item = ToolsLinks.AddItem(Loc.GetString("nc-meta-market-link", ("slot", link.Slot), ("interface", link.Capability),
                ("device", device?.Name ?? Loc.GetString("nc-meta-market-link-unassigned"))));
            item.Selected = link.Slot == _toolsSlot;
        }
        UpdateToolsLinkDevices(); UpdateToolsInstall();
    }
    private void UpdateToolsLinkDevices()
    {
        _toolsLinkDevices.Clear(); ToolsLinkDevice.Clear();
        ToolsLinkDevice.AddItem(Loc.GetString("nc-meta-market-link-unassigned"), 0);
        var link = _state.Links.FirstOrDefault(row => row.Slot == _toolsSlot);
        ToolsLinkDevice.Disabled = link == null;
        if (link == null) return;
        _toolsLinkDevices.AddRange(_state.Devices.Take(64).Where(row => row.Interfaces?.Contains(link.Capability) == true));
        for (var i = 0; i < _toolsLinkDevices.Count; i++) ToolsLinkDevice.AddItem(_toolsLinkDevices[i].Name, i + 1);
        ToolsLinkDevice.SelectId(_toolsBindings.TryGetValue(link.Slot, out var id) ? _toolsLinkDevices.FindIndex(row => row.DeviceId == id) + 1 : 0);
    }
    private void UpdateToolsInstall() => ToolsInstall.Disabled = !ToolsBindingConsent.Pressed || _state.ActiveReleaseId == Guid.Empty ||
        !_state.Devices.Any(row => row.DeviceId == _selectedDeviceId && row.CanInstall) ||
        _state.Links.Take(64).Any(link => !_toolsBindings.ContainsKey(link.Slot));
}
