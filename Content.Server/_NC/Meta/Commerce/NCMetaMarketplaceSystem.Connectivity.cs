// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    [Dependency] private NCMetaNetworkSystem _network = default!;
    private readonly HashSet<EntityUid> _marketplaceServices = [];
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaCitiNetServiceComponent, MapInitEvent>(OnMarketplaceServiceInit, after: [typeof(NCMetaDeviceRegistrySystem)]);
        SubscribeLocalEvent<NCMetaCitiNetServiceComponent, ComponentShutdown>(OnMarketplaceServiceShutdown);
    }
    private void OnMarketplaceServiceInit(Entity<NCMetaCitiNetServiceComponent> ent, ref MapInitEvent args)
    { if (_marketplaceServices.Count < 64) _marketplaceServices.Add(ent.Owner); }
    private void OnMarketplaceServiceShutdown(Entity<NCMetaCitiNetServiceComponent> ent, ref ComponentShutdown args) => _marketplaceServices.Remove(ent.Owner);

    public bool HasMarketplaceRoute(EntityUid source) => HasCitiNetRoute(source, requireMarketplace: true);

    /// <summary>A live native gateway and physical route establish connectivity; the segment label alone does not.</summary>
    public bool HasCitiNetRoute(EntityUid source, bool requireMarketplace = false)
    {
        if (!_devices.TryGetEntry(source, out var entry) || !entry.Ready || !_devices.IsPowered(source)) return false;
        foreach (var service in _marketplaceServices)
            if (TryComp<NCMetaCitiNetServiceComponent>(service, out var endpoint) && (!requireMarketplace || endpoint.SoftwareMarketplace) &&
                _devices.TryGetEntry(service, out var target) && target.Ready && _devices.IsPowered(service) &&
                TryComp<NCMetaPortComponent>(service, out var port) && port.NetworkId == "CITYNET" && _network.CanRoute(source, service)) return true;
        return false;
    }
    private bool CanContactMarketplace(string owner, string deviceId, string? accessDeviceId) =>
        _devices.TryResolve(accessDeviceId ?? deviceId, out var access) && access.Owner == owner && HasMarketplaceRoute(access.Entity) &&
        _devices.TryResolve(deviceId, out var target) && target.Owner == owner && _network.CanRoute(access.Entity, target.Entity);

    public async Task<MetaSoftwareCompatibility?> ReadCompatibilityAsync(string owner, string deviceId, Guid productId, Guid releaseId,
        string slotKind = "User")
    {
        var locator = await Read<NCMetaSoftwareReleaseLocator>("meta.software.release-index", productId.ToString("D") + ":" + releaseId.ToString("D"));
        var product = locator == null ? null : await ReadProductAsync(productId, locator.Major);
        var release = product?.Releases.SingleOrDefault(value => value.Id == releaseId);
        return release == null ? null : await Compatibility(owner, deviceId, release, slotKind);
    }
    public async Task<MetaSoftwareCompatibility?> ReadCompatibilityAsync(EntityUid terminal, EntityUid user, Guid productId,
        int major, Guid releaseId, string deviceId, string slotKind = "User")
    {
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return null;
        var owner = await Main(() => _devices.TryResolve(deviceId, out var target) ? target.Owner : null);
        if (owner == null || !await CanActFor(context, owner)) return null;
        var product = await ReadProductAsync(productId, major);
        var release = releaseId == Guid.Empty ? product?.Releases.LastOrDefault() : product?.Releases.SingleOrDefault(value => value.Id == releaseId);
        if (release == null || !await CanActFor(context, owner)) return null;
        var compatibility = await Compatibility(owner, deviceId, release, slotKind);
        return await CanActFor(context, owner) ? compatibility : null;
    }
    private async Task<MetaSoftwareCompatibility?> Compatibility(string owner, string deviceId, MetaSoftwareRelease release,
        string slotKind = "User")
    {
        if (slotKind is not ("User" or "Firmware")) return null;
        var usage = await _persistence.ReadSoftwareStorageUsageAsync(deviceId, owner, slotKind);
        if (usage == null) return null;
        return await Main(() =>
        {
            var row = CompatibilityHardware(owner, deviceId, release, slotKind);
            if (row == null) return null;
            var package = slotKind == "Firmware" ? "firmware" : release.PackageIdentity;
            var available = Math.Max(0, row.AvailableStorageBytes - usage.Bytes + usage.InheritedImageBytes.GetValueOrDefault(package));
            // Conservative upgrade admission retains the prior state until the new image is durable.
            var required = checked(row.RequiredStorageBytes + usage.UpgradeStateBytes.GetValueOrDefault(package));
            var accepted = row.Compatible && required <= available;
            return row with { AvailableStorageBytes = available, RequiredStorageBytes = required, Compatible = accepted,
                StorageRevision = usage.Revision, MessageKey = accepted ? "nc-meta-software-compatible" : "nc-meta-software-incompatible" };
        });
    }
    private MetaSoftwareCompatibility? CompatibilityHardware(string owner, string deviceId, MetaSoftwareRelease release, string slotKind)
    {
        if (!_devices.TryResolve(deviceId, out var device) || !device.Ready || device.Owner != owner ||
            !TryComp<NCMetaHostComponent>(device.Entity, out var host) || !_prototypes.TryIndex(host.Profile, out var profile)) return null;
        var missing = release.RequiredInterfaces.Where(api => !profile.Interfaces.Contains(api, StringComparer.Ordinal)).ToArray();
        var storage = TryComp<NCMetaStorageComponent>(device.Entity, out var volume) ? Math.Min(profile.StorageBytes, volume.CapacityBytes) : profile.StorageBytes;
        var share = profile.MemoryBytes / Math.Max(1, profile.MaxProcesses);
        var requiredMemory = checked(release.ResidentBytes + Math.Max(32768, release.MinimumProcessMemoryBytes));
        var compatible = (slotKind == "User" ? profile.AllowUserPackages : profile.AllowFirmwareUpdates) &&
            release.RuntimeAbi == profile.RuntimeAbi && !host.Disabled && profile.MaxProcesses > 0 && missing.Length == 0 && requiredMemory <= share &&
            release.MaximumConstantStringBytes <= 32768 && release.PackageBytes <= storage;
        return new(deviceId, profile.ID, compatible, missing, requiredMemory, share,
            release.PackageBytes, storage, compatible ? "nc-meta-software-compatible" : "nc-meta-software-incompatible")
            { SlotKind = slotKind };
    }
}
