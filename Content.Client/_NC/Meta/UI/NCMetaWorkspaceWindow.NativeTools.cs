// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text.Json;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    /// <summary>The surface the work area was last laid out for; only used to notice the change.</summary>
    private NCMetaSurface _laidOut = NCMetaSurface.Studio;
    private string _nativeToolsView = "";
    private Guid _toolsRelease;
    private string _toolsDevice = "";
    private string _toolsSlot = "";
    private readonly Dictionary<string, string> _toolsBindings = [];
    private readonly List<NCMetaDeviceSummary> _toolsLinkDevices = [];
    private readonly List<NCMetaInstallationSummary> _toolsInstallations = [];
    private float _logsElapsed = 99;
    private string _logsView = "";

    private void InitializeNativeTools()
    {
        TabContainer.SetTabTitle(NativeToolsPanel, Loc.GetString("nc-meta-tools-title"));
        TabContainer.SetTabVisible(NativeToolsPanel, false);
        // The software catalog and licences are not part of the game: the tab is never shown.
        TabContainer.SetTabVisible(SoftwareTab, false);
        ToolsInstaller.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "installer");
        ToolsRecovery.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "recovery");
        ToolsNetwork.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "network");
        ToolsHardware.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "hardware");
        ToolsDiagnostics.OnPressed += _ => Request(NCMetaWorkspaceOperation.OpenNativeTools, "diagnostics");
        ToolsMaintenance.OnPressed += _ =>
        {
            if (ToolsMaintenance.Disabled) return;
            var host = _state.NativeTools?.Hardware.FirstOrDefault(row => row.DeviceId == _selectedDeviceId);
            if (host != null) Request(NCMetaWorkspaceOperation.SetMaintenance, host.Maintenance ? "leave" : "enter");
        };
        ToolsLoadChip.OnPressed += _ =>
        {
            // The chip is the media device; the program it carries becomes the active project of this device and goes
            // through the ordinary bindings, consent and install below.
            if (_state.NativeTools is { ChipDeviceId.Length: > 0 } chip)
                Requested?.Invoke(new(NCMetaWorkspaceOperation.ImportMedia, Guid.NewGuid(), deviceId: chip.ChipDeviceId, projectId: _state.ProjectId));
        };
        ToolsLocalSites.OnItemSelected += args =>
        {
            if (_state.NativeTools?.LocalSites is { } sites && args.ItemIndex >= 0 && args.ItemIndex < sites.Count)
                Request(NCMetaWorkspaceOperation.OpenLocalSite, sites[args.ItemIndex].Address);
        };
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
        ToolsInstallations.OnItemSelected += args =>
        {
            if (_applying || args.ItemIndex < 0 || args.ItemIndex >= _toolsInstallations.Count) return;
            _selectedInstallationId = _toolsInstallations[args.ItemIndex].Id;
        };
        ToolsLogProgram.OnItemSelected += args =>
        {
            if (_applying || args.Id < 0 || args.Id >= _toolsInstallations.Count) return;
            ToolsLogProgram.SelectId(args.Id);
            _selectedInstallationId = _toolsInstallations[args.Id].Id;
            _logsElapsed = 99;
        };
        ToolsStart.OnPressed += _ => { if (_selectedInstallationId != Guid.Empty) Request(NCMetaWorkspaceOperation.Start); };
        ToolsPublishLocal.OnPressed += _ =>
        {
            // The view to publish: the first one the program declares, "main" by convention.
            if (_selectedInstallationId != Guid.Empty)
                Request(NCMetaWorkspaceOperation.PublishLocalSite, _state.ApplicationViews.FirstOrDefault() ?? "main");
        };
        ToolsStop.OnPressed += _ => { if (_selectedInstallationId != Guid.Empty) Request(NCMetaWorkspaceOperation.Stop); };
        ToolsBindingConsent.OnToggled += _ => UpdateToolsInstall();
        ToolsInstall.OnPressed += _ =>
        {
            if (!ToolsBindingConsent.Pressed || _state.ActiveReleaseId == Guid.Empty ||
                _state.Links.Take(64).Any(link => !_toolsBindings.ContainsKey(link.Slot))) return;
            var bindings = _state.Links.Take(64).Select(link => new NCMetaSoftwareBinding(link.Slot,
                _toolsBindings[link.Slot], link.ContractHash, link.RequiredScopes.ToList())).ToArray();
            Request(NCMetaWorkspaceOperation.Install, payload: new NCMetaBindingsPayload(bindings.ToList()));
        };
    }

    private void ApplyNativeTools()
    {
        var tools = _state.NativeTools;
        var mode = tools != null && _state.Surface == NCMetaSurface.ServicePanel;
        var view = tools?.View ?? "";
        var change = mode != (_laidOut == NCMetaSurface.ServicePanel) || view != _nativeToolsView;
        if (mode && _laidOut != NCMetaSurface.ServicePanel)
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
        _laidOut = mode ? NCMetaSurface.ServicePanel : NCMetaSurface.Studio; _nativeToolsView = view;
        // The service panel is an application of its own: no editor toolbar, no tab strip, the current section lit in the menu.
        MainToolbar.Visible = !mode;
        MainContentTabs.TabsVisible = !mode;
        if (mode) MoreRows.Visible = false;
        else MoreRows.Visible = MoreToggle.Pressed;
        ToolsInstaller.Pressed = view == "installer";
        ToolsHardware.Pressed = view == "hardware";
        ToolsNetwork.Pressed = view == "network";
        ToolsDiagnostics.Pressed = view == "diagnostics";
        ToolsRecovery.Pressed = view == "recovery";
        // On a computer the service panel is a section of the desktop; servers have no Studio to go back to.
        ToolsDesktop.Visible = mode && tools!.CanOpenStudio;
        NativeToolsMenu.Visible = mode; ProjectToolbar.Visible = !mode; ReleaseInfo.Visible = !mode;
        Save.Visible = Build.Visible = Tests.Visible = ExportMedia.Visible = ChipName.Visible = ChipDescription.Visible = !mode;
        TabContainer.SetTabVisible(EditorTab, !mode);
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
            tools.View == "diagnostics" ? "nc-meta-tools-diagnostics-help" : tools.View == "network" ? "nc-meta-tools-network-help" : "nc-meta-tools-description"));
        ToolsStockPanel.Visible = tools.View == "installer";
        ToolsLoadChip.Visible = tools.View == "installer" && tools.ChipDeviceId.Length > 0;
        ToolsLoadChip.Text = Loc.GetString("nc-meta-tools-load-chip", ("name", tools.ChipName));
        // The list views fill the height themselves; the scrolling part holds only what grows with its content.
        NativeToolsScroll.Visible = tools.View is "installer" or "network";
        MeasurementsCard.Visible = tools.View == "hardware";
        NetworkCard.Visible = LocalSitesCard.Visible = tools.View == "network";
        var maintenanceHost = tools.Hardware.FirstOrDefault(row => row.DeviceId == _selectedDeviceId);
        ToolsMaintenance.Visible = tools.View == "hardware" && maintenanceHost != null;
        if (maintenanceHost != null)
        {
            ToolsMaintenance.Text = Loc.GetString(maintenanceHost.Maintenance ? "nc-meta-hardware-maintenance-leave" : "nc-meta-hardware-maintenance-enter");
            ToolsMaintenance.ConfirmationText = Loc.GetString(maintenanceHost.Maintenance ? "nc-meta-tools-maintenance-leave-confirm" : "nc-meta-tools-maintenance-confirm");
        }
        LogsCard.Visible = tools.View is "diagnostics" or "recovery";
        if (_toolsRelease != _state.ActiveReleaseId || _toolsDevice != _selectedDeviceId)
        {
            _toolsRelease = _state.ActiveReleaseId; _toolsDevice = _selectedDeviceId;
            _toolsBindings.Clear(); _toolsSlot = ""; ToolsBindingConsent.Pressed = false;
            // A link written as a device code (link door: Door = "K7Q-4MD") proposes that device; the owner still approves it.
            foreach (var link in _state.Links.Take(64))
            {
                var code = link.Slot.Replace("-", "").Trim().ToUpperInvariant();
                var device = code.Length == 6 ? _state.Devices.Take(64).FirstOrDefault(row => row.DeviceId == code &&
                    row.Interfaces?.Contains(link.Capability) == true) : null;
                if (device != null) { _toolsBindings[link.Slot] = device.DeviceId; continue; }
                // Otherwise the device the player works on, when it offers what the link asks for: nobody types its code.
                var self = _state.Devices.Take(64).FirstOrDefault(row => row.DeviceId == _selectedDeviceId);
                if (self?.Interfaces?.Contains(link.Capability) == true) _toolsBindings[link.Slot] = self.DeviceId;
            }
        }
        var selectedStock = ToolsStock.SelectedId;
        ToolsStock.Clear();
        for (var i = 0; i < Math.Min(32, _state.Templates.Count); i++)
            ToolsStock.AddItem(_localization.DefaultCulture?.TwoLetterISOLanguageName == "ru" ? _state.Templates[i].NameRu : _state.Templates[i].Name, i);
        if (ToolsStock.ItemCount > 0) ToolsStock.SelectId(Math.Clamp(selectedStock, 0, ToolsStock.ItemCount - 1));
        ToolsPrepare.Disabled = ToolsStock.ItemCount == 0;
        ToolsStock.Visible = ToolsPrepare.Visible = ToolsStock.ItemCount > 0;
        UpdateToolsLinks();
        ToolsConnections.Visible = _state.Links.Count > 0 || _state.ActiveReleaseId != Guid.Empty;
        ToolsNoProgram.Visible = tools.ChipDeviceId.Length == 0 && _state.Links.Count == 0 && ToolsStock.ItemCount == 0;
        // The programs installed on this host, with Start and Stop: nothing runs until it is started.
        _toolsInstallations.Clear();
        _toolsInstallations.AddRange(_state.Installations.Where(installation => installation.DeviceId == _selectedDeviceId).Take(64));
        if (_selectedInstallationId == Guid.Empty && _toolsInstallations.Count == 1) _selectedInstallationId = _toolsInstallations[0].Id;
        ToolsInstallations.Clear();
        foreach (var installation in _toolsInstallations)
        {
            var item = ToolsInstallations.AddItem(Loc.GetString("nc-meta-tools-installation-row", ("name", installation.Name),
                ("startup", Loc.GetString(installation.AutoStart ? "nc-meta-installation-startup" : "nc-meta-installation-manual")),
                ("state", Loc.GetString(installation.Running ? "nc-meta-tools-state-running" : "nc-meta-tools-state-stopped"))));
            item.Selected = installation.Id == _selectedInstallationId;
        }
        ToolsStart.Disabled = ToolsStop.Disabled = ToolsPublishLocal.Disabled = _toolsInstallations.Count == 0;
        // The program whose log is shown: the one chosen in the list above, the only one, or the first.
        // A replaced program has a new identity: a stale choice falls back to the first program that exists now.
        if (_toolsInstallations.All(installation => installation.Id != _selectedInstallationId))
            _selectedInstallationId = _toolsInstallations.Count > 0 ? _toolsInstallations[0].Id : Guid.Empty;
        ToolsLogProgram.Clear();
        for (var logIndex = 0; logIndex < _toolsInstallations.Count; logIndex++)
            ToolsLogProgram.AddItem(_toolsInstallations[logIndex].Name, logIndex);
        var chosen = _toolsInstallations.FindIndex(installation => installation.Id == _selectedInstallationId);
        if (chosen >= 0) ToolsLogProgram.SelectId(chosen);
        ToolsLogProgram.Visible = _toolsInstallations.Count > 0;
        if (_logsView != view) { _logsView = view; _logsElapsed = 99; }
        ToolsMeasurements.Clear();
        foreach (var row in tools.Hardware.Take(64))
        {
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-hardware-row", ("name", row.Name), ("id", ShortDeviceId(row.DeviceId)),
                ("power", Loc.GetString(row.PowerKey)), ("health", Loc.GetString(row.HealthKey))), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-processes", ("used", row.Processes), ("limit", row.MaximumProcesses)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-links", ("used", row.LinkReserve), ("limit", row.LinkCapacity)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-memory", ("used", row.ReservedMemoryBytes), ("limit", row.MemoryBytes)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-gas", ("used", row.WindowGas), ("limit", row.GasPerWindow)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-load", ("load", row.LoadFactor), ("throttle", row.ThrottlePermille)), selectable: false);
            ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-power", ("watts", row.AppliedWatts)), selectable: false);
            ToolsMeasurements.AddItem(row.TemperatureKelvin is { } kelvin ? Loc.GetString("nc-meta-tools-temperature", ("kelvin", kelvin)) :
                Loc.GetString("nc-meta-tools-temperature-unavailable"), selectable: false);
            if (row.Reliability < 1000)
                ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-wear", ("wear", row.WearPermille / 10), ("reliability", row.Reliability / 10), ("failures", row.Failures)), selectable: false);
        }
        ToolsNetworkList.Clear();
        foreach (var device in tools.Network ?? [])
            ToolsNetworkList.AddItem(Loc.GetString(device.InUse ? "nc-meta-tools-network-row-used" : "nc-meta-tools-network-row",
                ("id", ShortDeviceId(device.DeviceId)), ("name", device.Name), ("cost", device.Cost)), selectable: false);
        ToolsLocalSites.Clear();
        foreach (var site in tools.LocalSites ?? [])
            ToolsLocalSites.AddItem(Loc.GetString("nc-meta-tools-local-site-row", ("host", site.Host), ("address", site.Address)));
        if (tools.Network is not { Count: > 0 }) ToolsNetworkList.AddItem(Loc.GetString("nc-meta-tools-network-empty"), selectable: false);
        if (tools.Hardware.Count == 0) ToolsMeasurements.AddItem(Loc.GetString("nc-meta-tools-no-hosts"), selectable: false);
        ToolsLogs.Clear(); foreach (var log in _state.Logs.TakeLast(256)) ToolsLogs.AddItem(log, selectable: false);
        if (_state.Logs.Count == 0) ToolsLogs.AddItem(Loc.GetString("nc-meta-tools-log-empty"), selectable: false);
    }

    /// <summary>People read and say device codes in groups of three: K7Q-4MD.</summary>
    private static string ShortDeviceId(string id) => id.Length == 6 ? id[..3] + "-" + id[3..] : id;

    /// <summary>The log of the chosen program is read again every two seconds while its view is open.</summary>
    private void TickNativeLogs(float seconds)
    {
        if (_surface != NCMetaSurface.ServicePanel || _nativeToolsView is not ("diagnostics" or "recovery") || _selectedInstallationId == Guid.Empty) return;
        _logsElapsed += seconds;
        if (_logsElapsed < 2) return;
        _logsElapsed = 0;
        Request(NCMetaWorkspaceOperation.Logs);
    }

    private void UpdateToolsLinks()
    {
        ToolsLinks.Clear();
        foreach (var link in _state.Links.Take(64))
        {
            var device = _toolsBindings.TryGetValue(link.Slot, out var id) ? _state.Devices.FirstOrDefault(row => row.DeviceId == id) : null;
            var item = ToolsLinks.AddItem(Loc.GetString("nc-meta-market-link", ("slot", link.Slot), ("interface", link.Capability),
                ("device", device == null ? Loc.GetString("nc-meta-market-link-unassigned") : device.Name + " [" + ShortDeviceId(device.DeviceId) + "]")));
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
        for (var i = 0; i < _toolsLinkDevices.Count; i++) ToolsLinkDevice.AddItem(_toolsLinkDevices[i].Name + " [" + ShortDeviceId(_toolsLinkDevices[i].DeviceId) + "]", i + 1);
        ToolsLinkDevice.SelectId(_toolsBindings.TryGetValue(link.Slot, out var id) ? _toolsLinkDevices.FindIndex(row => row.DeviceId == id) + 1 : 0);
    }
    private void UpdateToolsInstall() => ToolsInstall.Disabled = !ToolsBindingConsent.Pressed || _state.ActiveReleaseId == Guid.Empty ||
        !_state.Devices.Any(row => row.DeviceId == _selectedDeviceId && row.CanInstall) ||
        _state.Links.Take(64).Any(link => !_toolsBindings.ContainsKey(link.Slot));
}
