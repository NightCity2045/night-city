// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Client._NC.Meta.Editor;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private NCMetaCommerceState? _commerce;
    private Guid _softwareProductId;
    private Guid _softwareReleaseId;
    private Guid _softwareLicenseId;
    private string _softwareSeatDevice = "";
    private string _softwareOwner = "";
    private string _softwarePublisher = "";
    private Download? _softwareDownload;
    private string _softwareDownloadPath = "";
    private string _softwareSelectedPath = "";
    private Guid _softwareQuoteRequest;
    private bool _softwareQuoteAccepted;
    private readonly Dictionary<string, string> _softwareBindings = [];
    private readonly List<NCMetaDeviceSummary> _softwareLinkDevices = [];
    private string _softwareSlot = "";
    private Guid _softwarePublishingProduct;
    private Guid _softwareDiscardLicense;
    private string SoftwareSlotKind => SoftwareDeploymentSlot.SelectedId == 1 ? "Firmware" : "User";

    private NCMetaSoftwareProductDetails? SoftwareProduct => _commerce?.Product is { } product && product.ProductId == _softwareProductId ? product : null;
    private NCMetaSoftwareReleaseSummary? SoftwareRelease => SoftwareProduct?.Releases.FirstOrDefault(release => release.ReleaseId == _softwareReleaseId);
    private NCMetaSoftwareLicenseSummary? SoftwareLicense => _commerce?.Licenses.FirstOrDefault(license => license.LicenseId == _softwareLicenseId);

    private void InitializeCommerce()
    {
        TabContainer.SetTabTitle(EditorTab, Loc.GetString("nc-meta-market-editor"));
        TabContainer.SetTabTitle(SoftwareTab, Loc.GetString("nc-meta-market-tab"));
        TabContainer.SetTabTitle(SoftwareProductPanel, Loc.GetString("nc-meta-market-product"));
        TabContainer.SetTabTitle(SoftwarePublishPanel, Loc.GetString("nc-meta-market-publisher"));
        TabContainer.SetTabTitle(SoftwareSourcePanel, Loc.GetString("nc-meta-market-source"));
        MainContentTabs.OnTabChanged += tab => { if (tab == 1) RefreshCommerce(); };
        SoftwareRefresh.OnPressed += _ => RefreshCommerce();
        SoftwareCatalogPrevious.OnPressed += _ => RefreshCommerce(Math.Max(0, (_commerce?.CatalogOffset ?? 0) - 8));
        SoftwareCatalogNext.OnPressed += _ => RefreshCommerce((_commerce?.CatalogOffset ?? 0) + 8);
        SoftwareLicensePrevious.OnPressed += _ => RefreshCommerce(licenseOffset: Math.Max(0, (_commerce?.LicenseOffset ?? 0) - 8));
        SoftwareLicenseNext.OnPressed += _ => RefreshCommerce(licenseOffset: (_commerce?.LicenseOffset ?? 0) + 8);
        SoftwareCatalog.OnItemSelected += args =>
        {
            if (_applying || _commerce is null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(8, _commerce.Catalog.Count)) return;
            var product = _commerce.Catalog[args.ItemIndex];
            SelectSoftware(product.ProductId, product.Major);
        };
        SoftwareLicenses.OnItemSelected += args =>
        {
            if (_applying || _commerce is null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(8, _commerce.Licenses.Count)) return;
            var license = _commerce.Licenses[args.ItemIndex];
            _softwareLicenseId = license.LicenseId;
            SelectSoftware(license.ProductId, license.Major, preserveLicense: true);
        };
        SoftwareReleases.OnItemSelected += args =>
        {
            if (_applying || SoftwareProduct is not { } product || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, product.Releases.Count)) return;
            _softwareReleaseId = product.Releases[args.ItemIndex].ReleaseId;
            SoftwareScopeConsent.Pressed = false;
            InvalidateSoftwareQuote();
            _softwareBindings.Clear();
            _softwareSlot = "";
            ClearSoftwareSource();
            UpdateSoftwareDetails();
            CommerceRequest(NCMetaWorkspaceOperation.ReadSoftwareProduct, new NCMetaSoftwareSelection(product.ProductId, product.Major, _softwareReleaseId, SoftwareSlotKind));
        };
        SoftwareOwner.OnItemSelected += args =>
        {
            if (_applying) return;
            SoftwareOwner.SelectId(args.Id);
            _softwareOwner = _commerce is not null && args.Id >= 0 && args.Id < _commerce.Owners.Count ? _commerce.Owners[args.Id].Party : "";
            SoftwareSeatCount.Editable = SelectedSoftwareOwner()?.Organization == true;
            if (!SoftwareSeatCount.Editable) SoftwareSeatCount.Text = "1";
            InvalidateSoftwareQuote();
            RefreshCommerce();
        };
        SoftwarePublisherOwner.OnItemSelected += args =>
        {
            if (_applying) return;
            SoftwarePublisherOwner.SelectId(args.Id);
            _softwarePublisher = _commerce is not null && args.Id >= 0 && args.Id < _commerce.Owners.Count ? _commerce.Owners[args.Id].Party : "";
        };
        SoftwareQuote.OnPressed += _ => QuoteSoftware();
        AddChoices(SoftwareDeploymentSlot, ["slot-user", "slot-firmware"]);
        SoftwareDeploymentSlot.OnItemSelected += _ =>
        {
            if (_applying) return;
            InvalidateSoftwareQuote();
            InvalidateFirmware();
            SoftwareScopeConsent.Pressed = false;
            if (SoftwareProduct is { } product)
                CommerceRequest(NCMetaWorkspaceOperation.ReadSoftwareProduct,
                    new NCMetaSoftwareSelection(product.ProductId, product.Major, _softwareReleaseId, SoftwareSlotKind));
            UpdateSoftwareButtons();
        };
        SoftwareSeatCount.OnTextChanged += _ => { if (!_applying) InvalidateSoftwareQuote(); };
        SoftwarePurchase.OnPressed += _ =>
        {
            if (!_softwareQuoteAccepted || _commerce?.Quote is not { Consumed: false } quote || quote.ProductId != _softwareProductId ||
                quote.ReleaseId != _softwareReleaseId || quote.SlotKind != SoftwareSlotKind) return;
            InvalidateSoftwareQuote();
            CommerceRequest(NCMetaWorkspaceOperation.PurchaseSoftware, new NCMetaSoftwarePurchaseRequest(quote.QuoteId));
        };
        SoftwareScopeConsent.OnToggled += _ => UpdateSoftwareButtons();
        SoftwareLinks.OnItemSelected += args =>
        {
            if (_applying || SoftwareRelease?.Links is not { } links || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, links.Count)) return;
            _softwareSlot = links[args.ItemIndex].Slot;
            UpdateSoftwareLinkDevices();
        };
        SoftwareLinkDevice.OnItemSelected += args =>
        {
            if (_applying || _softwareSlot.Length == 0 || args.Id < 0 || args.Id > _softwareLinkDevices.Count) return;
            SoftwareLinkDevice.SelectId(args.Id);
            if (args.Id == 0) _softwareBindings.Remove(_softwareSlot);
            else _softwareBindings[_softwareSlot] = _softwareLinkDevices[args.Id - 1].DeviceId;
            SoftwareScopeConsent.Pressed = false;
            UpdateSoftwareLinks();
        };
        SoftwareInstall.OnPressed += _ => InstallSoftware();
        SoftwareAssignSeat.OnPressed += _ =>
        {
            if (SoftwareLicense is { CanAssign: true } license && _selectedDeviceId.Length > 0)
                CommerceRequest(NCMetaWorkspaceOperation.AssignSoftwareSeat, new NCMetaSoftwareSeatRequest(license.LicenseId, _selectedDeviceId));
        };
        SoftwareSeats.OnItemSelected += args =>
        {
            if (_applying || SoftwareLicense is not { } license || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, license.Seats.Count)) return;
            var seat = license.Seats[args.ItemIndex];
            _softwareSeatDevice = seat.DeviceId;
            SoftwareReleaseSeat.Disabled = !seat.CanRelease;
        };
        SoftwareReleaseSeat.OnPressed += _ =>
        {
            if (SoftwareLicense is { } license && license.Seats.Any(seat => seat.DeviceId == _softwareSeatDevice && seat.CanRelease))
                CommerceRequest(NCMetaWorkspaceOperation.ReleaseSoftwareSeat, new NCMetaSoftwareSeatRequest(license.LicenseId, _softwareSeatDevice));
        };
        SoftwareDiscardCopy.OnConfirming += _ => _softwareDiscardLicense = SoftwareLicense is { Illegal: true } license ? license.LicenseId : Guid.Empty;
        SoftwareDiscardCopy.OnPressed += _ =>
        {
            if (SoftwareLicense is { Illegal: true } license && license.LicenseId == _softwareDiscardLicense)
                CommerceRequest(NCMetaWorkspaceOperation.DiscardSoftwareCopy, new NCMetaSoftwareDiscardCopyRequest(license.LicenseId));
            _softwareDiscardLicense = Guid.Empty;
        };
        AddChoices(SoftwarePublishSource, ["source-closed", "source-licensee", "source-open"]);
        AddChoices(SoftwarePublishTransfer, ["transfer-none", "transfer-device", "transfer-portable"], 1);
        AddChoices(SoftwarePublishOffline, ["offline-perpetual", "offline-grace", "offline-online"]);
        AddChoices(SoftwarePublishUpdates, ["updates-major", "updates-supported"]);
        SoftwarePublishNew.OnToggled += _ =>
        {
            _softwarePublishingProduct = Guid.Empty;
            FillSoftwarePublisher();
            UpdateSoftwareButtons();
        };
        SoftwarePublish.OnPressed += _ => PublishSoftware();
        SoftwareSourcePath.OnItemSelected += args =>
        {
            if (_applying) return;
            SoftwareSourcePath.SelectId(args.Id);
            _softwareSelectedPath = SoftwareProduct?.SourcePaths?.ElementAtOrDefault(args.Id) ?? "";
            ClearSoftwareSource();
        };
        SoftwareReadSource.OnPressed += _ => ReadSoftwareSource();
        UpdateSoftwareButtons();
    }

    private static void AddChoices(OptionButton control, string[] keys, int selected = 0)
    {
        for (var index = 0; index < keys.Length; index++) control.AddItem(Loc.GetString("nc-meta-market-" + keys[index]), index);
        control.SelectId(selected);
        control.OnItemSelected += args => control.SelectId(args.Id);
    }

    private Guid CommerceRequest(NCMetaWorkspaceOperation operation, NCMetaWorkspacePayload payload, int utf8Offset = 0)
    {
        var id = Guid.NewGuid();
        Requested?.Invoke(new(operation, id, deviceId: _selectedDeviceId,
            releaseId: _state.ActiveReleaseId, projectId: _state.ProjectId, utf8Offset: utf8Offset, installationId: _selectedInstallationId, payload: payload));
        return id;
    }

    private void InvalidateSoftwareQuote()
    {
        _softwareQuoteAccepted = false;
        _softwareQuoteRequest = Guid.Empty;
        SoftwarePurchase.Disabled = true;
        SoftwarePrice.Text = "";
        SoftwarePrice.ToolTip = null;
    }

    private void RefreshCommerce(int? catalogOffset = null, int? licenseOffset = null, int? resaleOffset = null) => CommerceRequest(NCMetaWorkspaceOperation.BrowseSoftware,
        new NCMetaSoftwareBrowseRequest(catalogOffset ?? _commerce?.CatalogOffset ?? 0, licenseOffset ?? _commerce?.LicenseOffset ?? 0,
            _softwareOwner, resaleOffset ?? _commerce?.ResaleOffset ?? 0));

    private void SelectSoftware(Guid productId, int major, bool preserveLicense = false)
    {
        _softwareProductId = productId;
        _softwareReleaseId = Guid.Empty;
        if (!preserveLicense) _softwareLicenseId = Guid.Empty;
        _softwareSeatDevice = "";
        _softwareBindings.Clear();
        _softwareSlot = "";
        SoftwareScopeConsent.Pressed = false;
        InvalidateSoftwareQuote();
        _softwareSelectedPath = "";
        ClearSoftwareSource();
        UpdateSoftwareButtons();
        CommerceRequest(NCMetaWorkspaceOperation.ReadSoftwareProduct, new NCMetaSoftwareSelection(productId, major, SlotKind: SoftwareSlotKind));
    }

    private void ApplyCommerceState(NCMetaCommerceState? state)
    {
        if (state is null) { UpdateSoftwareButtons(); return; }
        _commerce = state;
        SoftwareCatalog.Clear();
        foreach (var product in state.Catalog.Take(8))
            SoftwareCatalog.AddItem(Loc.GetString("nc-meta-market-catalog-entry", ("name", product.Name), ("price", product.Price)));
        SoftwareLicenses.Clear();
        foreach (var license in state.Licenses.Take(8))
        {
            var item = SoftwareLicenses.AddItem(Loc.GetString("nc-meta-market-license-entry", ("name", license.ProductName),
                ("used", license.Seats.Count), ("limit", license.SeatLimit)));
            item.Selected = license.LicenseId == _softwareLicenseId;
        }
        SoftwareCatalogPrevious.Disabled = state.CatalogOffset <= 0;
        SoftwareCatalogNext.Disabled = !state.CatalogHasNext;
        SoftwareLicensePrevious.Disabled = state.LicenseOffset <= 0;
        SoftwareLicenseNext.Disabled = !state.LicenseHasNext;
        FillSoftwareOwners(SoftwareOwner, _softwareOwner);
        FillSoftwareOwners(SoftwarePublisherOwner, _softwarePublisher);
        if (_softwareOwner.Length == 0 && state.Owners.Count > 0) _softwareOwner = state.Owners[0].Party;
        if (_softwarePublisher.Length == 0 && state.Owners.Count > 0) _softwarePublisher = state.Owners[0].Party;
        SoftwareSeatCount.Editable = SelectedSoftwareOwner()?.Organization == true;
        if (!SoftwareSeatCount.Editable) SoftwareSeatCount.Text = "1";
        if (_softwareProductId == Guid.Empty && state.Product is { } initial) _softwareProductId = initial.ProductId;
        if (SoftwareProduct is { } productDetails)
        {
            var selected = productDetails.Releases.FirstOrDefault(release => release.ReleaseId == _softwareReleaseId) ?? productDetails.Releases.FirstOrDefault();
            if (_softwareReleaseId != (selected?.ReleaseId ?? Guid.Empty)) SoftwareScopeConsent.Pressed = false;
            _softwareReleaseId = selected?.ReleaseId ?? Guid.Empty;
        }
        SoftwareReleases.Clear();
        foreach (var release in SoftwareProduct?.Releases.Take(64) ?? [])
        {
            var item = SoftwareReleases.AddItem(release.Version);
            if (release.ReleaseId == _softwareReleaseId) item.Selected = true;
        }
        SoftwareSeats.Clear();
        foreach (var seat in SoftwareLicense?.Seats.Take(64) ?? []) SoftwareSeats.AddItem(seat.Label);
        SoftwareReleaseSeat.Disabled = true;
        _softwareSeatDevice = "";
        SoftwareSourcePath.Clear();
        var sourcePaths = SoftwareProduct?.SourcePaths?.Take(256).ToArray() ?? [];
        for (var index = 0; index < sourcePaths.Length; index++) SoftwareSourcePath.AddItem(sourcePaths[index], index);
        if (sourcePaths.Length > 0)
        {
            var selectedPath = Array.IndexOf(sourcePaths, _softwareSelectedPath);
            SoftwareSourcePath.SelectId(selectedPath >= 0 ? selectedPath : 0);
            _softwareSelectedPath = sourcePaths[SoftwareSourcePath.SelectedId];
        }
        if (_softwareQuoteRequest != Guid.Empty && _state.AcknowledgedRequestId == _softwareQuoteRequest)
        {
            _softwareQuoteAccepted = state.Quote is { Consumed: false };
            _softwareQuoteRequest = Guid.Empty;
        }
        if (_softwareQuoteAccepted && state.Quote is { } quote && quote.ProductId == _softwareProductId && quote.ReleaseId == _softwareReleaseId && quote.SlotKind == SoftwareSlotKind)
        {
            SoftwarePrice.Text = Loc.GetString("nc-meta-market-quote-details", ("name", quote.ProductName), ("version", quote.Version),
                ("price", quote.Price), ("owner", quote.OwnerLabel), ("device", quote.DeviceLabel), ("seats", quote.SeatCount), ("seconds", quote.ExpiresInSeconds));
            SoftwarePrice.ToolTip = SoftwarePrice.Text + "\n" + string.Join("\n", SoftwareTerms(quote.Terms));
            SoftwarePurchase.Disabled = quote.Consumed;
        }
        else
        {
            _softwareQuoteAccepted = false;
            SoftwarePrice.Text = "";
            SoftwarePrice.ToolTip = null;
            SoftwarePurchase.Disabled = true;
        }
        UpdateSoftwareDetails();
        UpdateSoftwareLinks();
        FillSoftwarePublisher();
        ApplySoftwareResale();
        if (state.Source is { } source) ApplySoftwareSource(source);
        UpdateSoftwareButtons();
    }

    private void FillSoftwareOwners(OptionButton control, string selected)
    {
        control.Clear();
        var owners = _commerce?.Owners.Take(64).ToArray() ?? [];
        if (owners.Length == 0) { control.AddItem(Loc.GetString("nc-meta-market-current-owner"), 0); control.SelectId(0); return; }
        for (var index = 0; index < owners.Length; index++) control.AddItem(owners[index].Label, index);
        var chosen = Array.FindIndex(owners, owner => owner.Party == selected);
        control.SelectId(chosen >= 0 ? chosen : 0);
    }

    private NCMetaSoftwareOwnerSummary? SelectedSoftwareOwner() => _commerce?.Owners.FirstOrDefault(owner => owner.Party == _softwareOwner);

    private void QuoteSoftware()
    {
        if (SoftwareProduct is not { } product || SoftwareRelease is not { } release || _selectedDeviceId.Length == 0 ||
            !int.TryParse(SoftwareSeatCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seats) || seats is < 1 or > 64)
        { Status.Text = Loc.GetString("nc-meta-market-invalid"); return; }
        var organization = SelectedSoftwareOwner()?.Organization == true;
        if (!organization && seats != 1) { Status.Text = Loc.GetString("nc-meta-market-invalid"); return; }
        InvalidateSoftwareQuote();
        _softwareQuoteRequest = CommerceRequest(NCMetaWorkspaceOperation.QuoteSoftware, new NCMetaSoftwareQuoteRequest(product.ProductId, product.Major,
            release.ReleaseId, _softwareOwner, organization ? NCMetaSoftwareLicenseScope.Organization : NCMetaSoftwareLicenseScope.Device, seats, SoftwareSlotKind));
    }

    private void InstallSoftware()
    {
        if (SoftwareSlotKind == "Firmware")
        {
            if (SoftwareProduct is { } firmwareProduct && SoftwareRelease is { } firmwareRelease)
                PreviewFirmware(firmwareProduct.ProductId, firmwareRelease.ReleaseId);
            return;
        }
        if (SoftwareProduct is not { } product || SoftwareRelease is not { Links: { } links } release || !SoftwareScopeConsent.Pressed) return;
        if (links.Count > 64 || links.Any(link => !HasSoftwareBinding(link) || link.RequiredScopes.Count > 64))
        { Status.Text = Loc.GetString("nc-meta-market-invalid"); return; }
        var bindings = links.Select(link => new NCMetaSoftwareBinding(link.Slot, _softwareBindings[link.Slot],
            link.ContractHash, link.RequiredScopes.ToList())).ToList();
        CommerceRequest(NCMetaWorkspaceOperation.InstallLicensedSoftware,
            new NCMetaSoftwareInstallRequest(product.ProductId, release.ReleaseId, bindings, true, release.RequiredScopes.Take(64).ToList()));
    }

    private void PublishSoftware()
    {
        var description = Rope.Collapse(SoftwarePublishDescription.TextRope);
        var changelog = Rope.Collapse(SoftwarePublishChangelog.TextRope);
        if (SoftwarePublishName.Text.Length is < 1 or > 64 || Encoding.UTF8.GetByteCount(SoftwarePublishName.Text) > 256 ||
            Encoding.UTF8.GetByteCount(description) > 2048 || Encoding.UTF8.GetByteCount(changelog) > 2048 ||
            !long.TryParse(SoftwarePublishPrice.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var price) || price is < 0 or > 1000000 ||
            !long.TryParse(SoftwarePublishGrace.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var grace) || grace is < 0 or > 2592000 ||
            !long.TryParse(SoftwarePublishSupport.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var support) || support is < 0 or > 31536000 ||
            !SoftwarePublishNew.Pressed && SoftwareProduct is not { CanPublish: true })
        { Status.Text = Loc.GetString("nc-meta-market-invalid"); return; }
        var offline = (NCMetaSoftwareOfflinePolicy) SoftwarePublishOffline.SelectedId;
        if (offline == NCMetaSoftwareOfflinePolicy.GracePeriod && grace == 0 || offline != NCMetaSoftwareOfflinePolicy.GracePeriod && grace != 0)
        { Status.Text = Loc.GetString("nc-meta-market-invalid"); return; }
        var terms = new NCMetaSoftwareTerms((NCMetaSoftwareSourcePolicy) SoftwarePublishSource.SelectedId,
            (NCMetaSoftwareTransferPolicy) SoftwarePublishTransfer.SelectedId, offline,
            (NCMetaSoftwareUpdatePolicy) SoftwarePublishUpdates.SelectedId, grace, support, SoftwarePublishResale.Pressed,
            SoftwarePublishBuild.Pressed, SoftwarePublishOrganizations.Pressed, SoftwarePublishRedistribution.Pressed);
        CommerceRequest(NCMetaWorkspaceOperation.PublishSoftware, new NCMetaSoftwarePublishRequest(
            SoftwarePublishNew.Pressed ? Guid.Empty : _softwareProductId, SoftwarePublishName.Text, description, changelog, price, terms, _softwarePublisher));
    }

    private void FillSoftwarePublisher()
    {
        if (SoftwarePublishNew.Pressed || SoftwareProduct is not { CanPublish: true } product || _softwarePublishingProduct == product.ProductId) return;
        _softwarePublishingProduct = product.ProductId;
        SoftwarePublishName.Text = product.Name;
        SoftwarePublishDescription.TextRope = new Rope.Leaf(product.Description);
        SoftwarePublishPrice.Text = product.Price.ToString(CultureInfo.InvariantCulture);
        SoftwarePublishSource.SelectId((int) product.Terms.Source);
        SoftwarePublishTransfer.SelectId((int) product.Terms.Transfer);
        SoftwarePublishOffline.SelectId((int) product.Terms.Offline);
        SoftwarePublishUpdates.SelectId((int) product.Terms.Updates);
        SoftwarePublishGrace.Text = product.Terms.OfflineGraceSeconds.ToString(CultureInfo.InvariantCulture);
        SoftwarePublishSupport.Text = product.Terms.SupportSeconds.ToString(CultureInfo.InvariantCulture);
        SoftwarePublishResale.Pressed = product.Terms.AllowResale;
        SoftwarePublishBuild.Pressed = product.Terms.AllowLibraryBuild;
        SoftwarePublishOrganizations.Pressed = product.Terms.AllowOrganizationSeats;
        SoftwarePublishRedistribution.Pressed = product.Terms.AllowRedistribution;
    }

    private void ReadSoftwareSource(int offset = 0)
    {
        if (SoftwareProduct is not { CanReadSource: true } product || SoftwareRelease is not { } release ||
            product.SourcePaths is not { } paths || SoftwareSourcePath.SelectedId < 0 || SoftwareSourcePath.SelectedId >= Math.Min(256, paths.Count)) return;
        var path = offset == 0 ? paths[SoftwareSourcePath.SelectedId] : _softwareDownloadPath;
        if (!paths.Contains(path)) return;
        if (offset == 0) { ClearSoftwareSource(); _softwareDownloadPath = path; }
        var license = SoftwareLicense;
        CommerceRequest(NCMetaWorkspaceOperation.ReadSoftwareSource, new NCMetaSoftwareSourceRequest(product.ProductId, release.ReleaseId,
            path, license?.LicenseId, license?.Owner ?? _softwareOwner), offset);
    }

    private void ApplySoftwareSource(NCMetaSoftwareSourceContent source)
    {
        if (source.ProductId != _softwareProductId || source.ReleaseId != _softwareReleaseId || source.Path != _softwareDownloadPath || source.Sha256.Length != 64) return;
        var bytes = Encoding.UTF8.GetBytes(source.Source);
        var total = source.TotalUtf8Bytes == 0 && source.Complete ? bytes.Length : source.TotalUtf8Bytes;
        if (bytes.Length > 32 * 1024 || !source.Complete && bytes.Length == 0 || total is < 0 or > NCMetaCodeEditor.MaximumBytes || source.Utf8Offset < 0 || source.Utf8Offset > total - bytes.Length) return;
        if (source.Utf8Offset == 0) _softwareDownload = new(total, 0, source.Sha256);
        if (_softwareDownload is not { } download || download.Bytes.Length != total || download.Sha256 != source.Sha256 || download.Received != source.Utf8Offset) return;
        bytes.CopyTo(download.Bytes.AsSpan(download.Received));
        download.Received += bytes.Length;
        if (!source.Complete) { ReadSoftwareSource(download.Received); return; }
        _softwareDownload = null;
        if (download.Received != total || !string.Equals(Content.Client._NC.Meta.Editor.NCMetaSourceHash.ToHexString(Content.Client._NC.Meta.Editor.NCMetaSourceHash.HashData(download.Bytes)), source.Sha256, StringComparison.OrdinalIgnoreCase))
        { SoftwareSourceStatus.Text = Loc.GetString("nc-meta-market-source-invalid"); return; }
        SoftwareSourceEditor.TrySetSource(Encoding.UTF8.GetString(download.Bytes));
        SoftwareSourceStatus.Text = source.Path;
    }

    private void ClearSoftwareSource()
    {
        _softwareDownload = null;
        _softwareDownloadPath = "";
        SoftwareSourceEditor.TrySetSource("");
        SoftwareSourceStatus.Text = "";
    }

    private void UpdateSoftwareButtons()
    {
        var target = _state.Devices.Take(64).Any(device => device.DeviceId == _selectedDeviceId);
        var release = SoftwareRelease;
        SoftwareQuote.Disabled = !target || release is null;
        SoftwareInstall.Text = Loc.GetString(SoftwareSlotKind == "Firmware" ? "nc-meta-firmware-preview" : "nc-meta-market-install");
        SoftwareInstall.Disabled = !target || release?.Links is not { } links || SoftwareSlotKind == "User" &&
            (!SoftwareScopeConsent.Pressed || links.Any(link => !HasSoftwareBinding(link)));
        SoftwareAssignSeat.Disabled = !target || SoftwareLicense is not { CanAssign: true };
        SoftwareDiscardCopy.Disabled = SoftwareLicense is not { Illegal: true };
        SoftwareReadSource.Disabled = SoftwareProduct is not { CanReadSource: true, SourcePaths: { Count: > 0 } } || SoftwareRelease is null;
        SoftwarePublish.Disabled = _state.ActiveReleaseId == Guid.Empty || !SoftwarePublishNew.Pressed && SoftwareProduct is not { CanPublish: true };
        UpdateSoftwareResaleButtons();
    }

    private bool HasSoftwareBinding(NCMetaLinkSummary link) => _softwareBindings.TryGetValue(link.Slot, out var id) &&
        _state.Devices.Take(64).Any(device => device.DeviceId == id && device.Interfaces?.Contains(link.Capability) == true);

    private void UpdateSoftwareLinks()
    {
        var applying = _applying;
        _applying = true;
        SoftwareLinks.Clear();
        foreach (var link in SoftwareRelease?.Links?.Take(64) ?? [])
        {
            var device = _softwareBindings.TryGetValue(link.Slot, out var id)
                ? _state.Devices.Take(64).FirstOrDefault(candidate => candidate.DeviceId == id) : null;
            var item = SoftwareLinks.AddItem(Loc.GetString("nc-meta-market-link", ("slot", link.Slot),
                ("interface", link.Capability), ("device", device?.Name ?? Loc.GetString("nc-meta-market-link-unassigned"))));
            if (link.Slot == _softwareSlot) item.Selected = true;
        }
        UpdateSoftwareLinkDevices();
        _applying = applying;
        UpdateSoftwareButtons();
    }

    private void UpdateSoftwareLinkDevices()
    {
        _softwareLinkDevices.Clear();
        SoftwareLinkDevice.Clear();
        SoftwareLinkDevice.AddItem(Loc.GetString("nc-meta-market-link-unassigned"), 0);
        var link = SoftwareRelease?.Links?.FirstOrDefault(candidate => candidate.Slot == _softwareSlot);
        SoftwareLinkDevice.Disabled = link is null;
        if (link is null) return;
        _softwareLinkDevices.AddRange(_state.Devices.Take(64).Where(device => device.Interfaces?.Contains(link.Capability) == true));
        for (var index = 0; index < _softwareLinkDevices.Count; index++)
            SoftwareLinkDevice.AddItem(_softwareLinkDevices[index].Name, index + 1);
        var selected = _softwareBindings.TryGetValue(link.Slot, out var id)
            ? _softwareLinkDevices.FindIndex(device => device.DeviceId == id) : -1;
        SoftwareLinkDevice.SelectId(selected + 1);
    }

    private void UpdateSoftwareDetails()
    {
        SoftwareDetails.Clear();
        if (SoftwareProduct is not { } product)
        { SoftwareName.Text = SoftwareDescription.Text = ""; return; }
        SoftwareName.Text = Loc.GetString("nc-meta-market-product-heading", ("name", product.Name), ("publisher", product.PublisherName));
        SoftwareDescription.Text = product.Description;
        SoftwareDescription.ToolTip = product.Description;
        var terms = _softwareQuoteAccepted && _commerce?.Quote is { } quote && quote.ProductId == _softwareProductId && quote.ReleaseId == _softwareReleaseId
            ? quote.Terms : product.Terms;
        if (SoftwareLicense is not { Illegal: true })
            foreach (var line in SoftwareTerms(terms)) SoftwareDetails.AddItem(line, selectable: false);
        if (SoftwareLicense is { } license)
        {
            SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-license-owner", ("owner", license.OwnerLabel)), selectable: false);
            if (license.Illegal)
            {
                SoftwareDetails.AddItem(Loc.GetString(license.IllegalExpiresInSeconds > 0 ? "nc-meta-market-illegal-expiry" : "nc-meta-market-illegal-expired",
                    ("seconds", Math.Clamp(license.IllegalExpiresInSeconds, 0, 1800))), selectable: false);
                SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-illegal-rights"), selectable: false);
            }
            if (license.Revoked || license.Illegal) SoftwareDetails.AddItem(Loc.GetString(license.Revoked ? "nc-meta-market-revoked" : "nc-meta-market-unverified"), selectable: false);
        }
        if (SoftwareRelease is not { } release) return;
        SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-release-details", ("version", release.Version), ("abi", release.RuntimeAbi), ("bytes", release.ResidentBytes)), selectable: false);
        SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-release-resources", ("memory", release.ProcessMemoryBytes), ("storage", release.PackageBytes)), selectable: false);
        SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-release-digest", ("digest", release.ArtifactDigest)), selectable: false);
        var compatible = release.Compatibility ?? (_softwareQuoteAccepted ? _commerce?.Quote?.Compatibility : null);
        if (compatible is { } compatibility)
        {
            foreach (var device in compatibility.Take(8))
            {
                SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-compatibility-device", ("device", device.DeviceLabel),
                    ("hardware", device.HardwareName), ("result", Loc.GetString(device.Compatible ? "nc-meta-market-compatible" : "nc-meta-market-incompatible"))), selectable: false);
                SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-compatibility-memory", ("required", device.RequiredMemoryBytes), ("available", device.AvailableMemoryBytes)), selectable: false);
                SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-compatibility-storage", ("required", device.RequiredStorageBytes), ("available", device.AvailableStorageBytes)), selectable: false);
                foreach (var api in device.MissingInterfaces.Take(64)) SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-missing-interface", ("interface", api)), selectable: false);
                if (device.MessageKey.Length > 0) SoftwareDetails.AddItem(Loc.GetString(device.MessageKey), selectable: false);
            }
        }
        foreach (var scope in release.RequiredScopes.Take(64)) SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-scope", ("scope", scope)), selectable: false);
        foreach (var api in release.RequiredInterfaces.Take(64)) SoftwareDetails.AddItem(Loc.GetString("nc-meta-market-interface", ("interface", api)), selectable: false);
        if (release.Changelog.Length > 0) SoftwareDetails.AddItem(release.Changelog, selectable: false);
    }

    private static IEnumerable<string> SoftwareTerms(NCMetaSoftwareTerms terms)
    {
        yield return Loc.GetString(terms.Source switch { NCMetaSoftwareSourcePolicy.Closed => "nc-meta-market-source-closed", NCMetaSoftwareSourcePolicy.Licensee => "nc-meta-market-source-licensee", NCMetaSoftwareSourcePolicy.Open => "nc-meta-market-source-open", _ => "nc-meta-market-unknown-policy" });
        yield return Loc.GetString(terms.Transfer switch { NCMetaSoftwareTransferPolicy.None => "nc-meta-market-transfer-none", NCMetaSoftwareTransferPolicy.WithDevice => "nc-meta-market-transfer-device", NCMetaSoftwareTransferPolicy.Portable => "nc-meta-market-transfer-portable", _ => "nc-meta-market-unknown-policy" });
        yield return Loc.GetString(terms.Offline switch { NCMetaSoftwareOfflinePolicy.ActivatedPerpetual => "nc-meta-market-offline-perpetual", NCMetaSoftwareOfflinePolicy.GracePeriod => "nc-meta-market-offline-grace", NCMetaSoftwareOfflinePolicy.OnlineRequired => "nc-meta-market-offline-online", _ => "nc-meta-market-unknown-policy" });
        yield return Loc.GetString(terms.Updates switch { NCMetaSoftwareUpdatePolicy.SameMajor => "nc-meta-market-updates-major", NCMetaSoftwareUpdatePolicy.SupportedReleases => "nc-meta-market-updates-supported", _ => "nc-meta-market-unknown-policy" });
        yield return Loc.GetString("nc-meta-market-term-windows", ("grace", terms.OfflineGraceSeconds), ("support", terms.SupportSeconds));
        if (terms.AllowResale) yield return Loc.GetString("nc-meta-market-allow-resale");
        if (terms.AllowLibraryBuild) yield return Loc.GetString("nc-meta-market-allow-build");
        if (terms.AllowOrganizationSeats) yield return Loc.GetString("nc-meta-market-allow-organizations");
        if (terms.AllowRedistribution) yield return Loc.GetString("nc-meta-market-allow-redistribution");
    }
}
