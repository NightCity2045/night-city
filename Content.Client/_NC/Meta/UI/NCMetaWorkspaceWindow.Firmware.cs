// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private NCMetaFirmwarePreview? _firmware;
    private Guid _firmwarePreviewRequest;
    private string _firmwareDevice = "";
    private Guid _firmwareInstallation;
    private Guid _firmwareProject;
    private Guid _firmwareProduct;
    private Guid _firmwareRelease;
    private readonly Dictionary<string, string> _firmwareBindings = [];
    private readonly List<NCMetaDeviceSummary> _firmwareLinkDevices = [];
    private string _firmwareSlot = "";
    private NCMetaFirmwareInstallWindow? _firmwareDialog;

    private void InitializeFirmware()
    {
        TabContainer.SetTabTitle(FirmwarePanel, Loc.GetString("nc-meta-firmware-tab"));
        FirmwarePreview.OnPressed += _ =>
        {
            if (_firmwareProduct != Guid.Empty && FirmwareSourceIsCurrent()) PreviewFirmware(_firmwareProduct, _firmwareRelease);
            else PreviewFirmware();
        };
        FirmwareLinks.OnItemSelected += args =>
        {
            if (_applying || _firmware is null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, _firmware.Links.Count)) return;
            _firmwareSlot = _firmware.Links[args.ItemIndex].Slot;
            UpdateFirmwareLinkDevices();
        };
        FirmwareLinkDevice.OnItemSelected += args =>
        {
            if (_applying || _firmwareSlot.Length == 0 || args.Id < 0 || args.Id > _firmwareLinkDevices.Count) return;
            if (args.Id == 0) _firmwareBindings.Remove(_firmwareSlot);
            else _firmwareBindings[_firmwareSlot] = _firmwareLinkDevices[args.Id - 1].DeviceId;
            UpdateFirmwareControls();
        };
        FirmwareInstall.OnPressed += _ => ReviewFirmware();
        UpdateFirmwareControls();
    }

    private void ApplyFirmwareState(NCMetaFirmwarePreview? preview)
    {
        if (_firmwareDevice != _selectedDeviceId || _firmwareInstallation != _selectedInstallationId || _firmwareProject != _state.ProjectId ||
            !FirmwareSourceIsCurrent())
            InvalidateFirmware();
        if (preview is not null && _firmwarePreviewRequest != Guid.Empty && _state.AcknowledgedRequestId == _firmwarePreviewRequest &&
            preview.ReleaseId == _firmwareRelease && preview.ProductId == _firmwareProduct)
        {
            _firmwarePreviewRequest = Guid.Empty;
            _firmware = preview;
        }
        UpdateFirmwareControls();
    }

    private bool FirmwareSourceIsCurrent() => _firmwareProduct == Guid.Empty
        ? _firmwareRelease == _state.ActiveReleaseId
        : SoftwareProduct?.ProductId == _firmwareProduct && SoftwareRelease?.ReleaseId == _firmwareRelease;

    private void PreviewFirmware(Guid productId = default, Guid releaseId = default)
    {
        InvalidateFirmware();
        _firmwareProduct = productId;
        _firmwareRelease = productId == Guid.Empty ? _state.ActiveReleaseId : releaseId;
        _firmwareDevice = _selectedDeviceId;
        _firmwareInstallation = _selectedInstallationId;
        _firmwareProject = _state.ProjectId;
        _firmwarePreviewRequest = Guid.NewGuid();
        MainContentTabs.CurrentTab = 0;
        Output.CurrentTab = 7;
        UpdateFirmwareControls();
        FirmwareNotice.Text = Loc.GetString("nc-meta-firmware-checking");
        Requested?.Invoke(new(NCMetaWorkspaceOperation.PreviewFirmware, _firmwarePreviewRequest,
            source: JsonSerializer.Serialize(new NCMetaFirmwareSelection(productId)),
            deviceId: _firmwareDevice, releaseId: _firmwareRelease, projectId: _firmwareProject,
            installationId: SelectedInstallation?.Slot == "Firmware" ? _firmwareInstallation : Guid.Empty));
    }

    private void InvalidateFirmware()
    {
        _firmware = null;
        _firmwarePreviewRequest = Guid.Empty;
        _firmwareProduct = Guid.Empty;
        _firmwareRelease = Guid.Empty;
        _firmwareBindings.Clear();
        _firmwareSlot = "";
        _firmwareDialog?.Dispose();
        _firmwareDialog = null;
    }

    private bool HasFirmwareBinding(NCMetaLinkSummary link) => _firmwareBindings.TryGetValue(link.Slot, out var id) &&
        _state.Devices.Take(64).Any(device => device.DeviceId == id && device.Interfaces?.Contains(link.Capability) == true);

    private void UpdateFirmwareControls()
    {
        var applying = _applying;
        _applying = true;
        FirmwarePreview.Disabled = _state.ActiveReleaseId == Guid.Empty || !_state.Devices.Take(64).Any(device => device.DeviceId == _selectedDeviceId);
        FirmwareDetails.Clear();
        FirmwareLinks.Clear();
        FirmwareNotice.Text = "";
        if (_firmware is { } preview)
        {
            FirmwareDetails.AddItem(Loc.GetString("nc-meta-firmware-package", ("package", preview.PackageName), ("version", preview.Version)), selectable: false);
            FirmwareDetails.AddItem(Loc.GetString("nc-meta-firmware-artifact", ("bytes", preview.ArtifactResidentBytes)), selectable: false);
            FirmwareDetails.AddItem(Loc.GetString("nc-meta-firmware-memory", ("bytes", preview.ProcessMemoryBytes)), selectable: false);
            FirmwareDetails.AddItem(Loc.GetString("nc-meta-firmware-storage", ("bytes", preview.PackageBytes)), selectable: false);
            FirmwareDetails.AddItem(Loc.GetString(preview.Maintenance ? "nc-meta-firmware-maintenance-on" : "nc-meta-firmware-maintenance-off"), selectable: false);
            foreach (var scope in preview.RequiredScopes.Take(64)) FirmwareDetails.AddItem(Loc.GetString("nc-meta-market-scope", ("scope", scope)), selectable: false);
            FirmwareNotice.Text = preview.NoticeKey.Length == 0 ? "" : Loc.GetString(preview.NoticeKey);
            FirmwareNotice.ToolTip = FirmwareNotice.Text;
            foreach (var link in preview.Links.Take(64))
            {
                var label = _firmwareBindings.TryGetValue(link.Slot, out var id)
                    ? _state.Devices.Take(64).FirstOrDefault(device => device.DeviceId == id)?.Name : null;
                var item = FirmwareLinks.AddItem(Loc.GetString("nc-meta-market-link", ("slot", link.Slot), ("interface", link.Capability),
                    ("device", label ?? Loc.GetString("nc-meta-market-link-unassigned"))));
                item.Selected = link.Slot == _firmwareSlot;
            }
        }
        UpdateFirmwareLinkDevices();
        FirmwareInstall.Disabled = _firmware is not { Eligible: true } admitted || admitted.Links.Any(link => !HasFirmwareBinding(link));
        _applying = applying;
    }

    private void UpdateFirmwareLinkDevices()
    {
        FirmwareLinkDevice.Clear();
        FirmwareLinkDevice.AddItem(Loc.GetString("nc-meta-market-link-unassigned"), 0);
        _firmwareLinkDevices.Clear();
        var link = _firmware?.Links.FirstOrDefault(candidate => candidate.Slot == _firmwareSlot);
        FirmwareLinkDevice.Disabled = link is null;
        if (link is null) return;
        _firmwareLinkDevices.AddRange(_state.Devices.Take(64).Where(device => device.Interfaces?.Contains(link.Capability) == true));
        for (var index = 0; index < _firmwareLinkDevices.Count; index++) FirmwareLinkDevice.AddItem(_firmwareLinkDevices[index].Name, index + 1);
        var selected = _firmwareBindings.TryGetValue(link.Slot, out var id) ? _firmwareLinkDevices.FindIndex(device => device.DeviceId == id) : -1;
        FirmwareLinkDevice.SelectId(selected + 1);
    }

    private void ReviewFirmware()
    {
        if (FirmwareInstall.Disabled || _firmware is not { Eligible: true } preview || preview.RequiredScopes.Count > 64 || preview.Links.Count > 64 ||
            _firmwareDevice != _selectedDeviceId || _firmwareInstallation != _selectedInstallationId || _firmwareProject != _state.ProjectId ||
            !FirmwareSourceIsCurrent() || preview.ReleaseId != _firmwareRelease || preview.Links.Any(link => !HasFirmwareBinding(link))) return;
        var bindings = preview.Links.Select(link => new NCMetaSoftwareBinding(link.Slot, _firmwareBindings[link.Slot],
            link.ContractHash, link.RequiredScopes.Take(64).ToList())).ToList();
        var device = _state.Devices.Take(64).FirstOrDefault(candidate => candidate.DeviceId == _firmwareDevice);
        if (device is null) return;
        var linkLabels = preview.Links.Select(link => Loc.GetString("nc-meta-market-link", ("slot", link.Slot), ("interface", link.Capability),
            ("device", _state.Devices.Take(64).First(candidate => candidate.DeviceId == _firmwareBindings[link.Slot]).Name))).ToList();
        _firmwareDialog?.Dispose();
        var dialog = new NCMetaFirmwareInstallWindow(device.Name, preview, linkLabels);
        _firmwareDialog = dialog;
        var targetDevice = _firmwareDevice;
        var targetInstallation = _firmwareInstallation;
        var targetProject = _firmwareProject;
        dialog.Confirmed += () =>
        {
            if (_firmware != preview || _selectedDeviceId != targetDevice || _selectedInstallationId != targetInstallation ||
                _state.ProjectId != targetProject || !FirmwareSourceIsCurrent()) return;
            var request = new NCMetaFirmwareInstallRequest(bindings, true, preview.RequiredScopes.Take(64).ToList(), true,
                preview.ArtifactResidentBytes, preview.ProcessMemoryBytes, preview.PackageBytes,
                preview.ProductId, preview.InstallationId, preview.InstallationRevision);
            var source = JsonSerializer.Serialize(request);
            if (Encoding.UTF8.GetByteCount(source) > 32 * 1024)
            { Status.Text = Loc.GetString("nc-meta-firmware-request-large"); return; }
            Requested?.Invoke(new(NCMetaWorkspaceOperation.InstallFirmware, Guid.NewGuid(), source: source,
                deviceId: targetDevice, releaseId: preview.ReleaseId, projectId: targetProject, installationId: preview.InstallationId));
        };
        dialog.OnClose += () => { if (ReferenceEquals(_firmwareDialog, dialog)) _firmwareDialog = null; dialog.Dispose(); };
        dialog.OpenCentered();
    }
}
