// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    public async Task<(string Owner, bool Organization)[]> ReadManageableOwnersAsync(EntityUid terminal, EntityUid user)
    {
        var context = await Main(() => Capture(terminal, user));
        if (context == null || !NCPartyRef.TryParse(context.Actor, out var actor) || !actor.TryGetCharacter(out var character)) return [];
        var job = await _database.GetNCActiveJobAsync(new Content.Shared._NC.Identity.NCCharacterId(character.Value));
        return await Main(() =>
        {
            if (!Current(context)) return [];
            var result = new List<(string Owner, bool Organization)> { (context.Actor, false) };
            if (job != null)
                foreach (var department in _prototypes.EnumeratePrototypes<DepartmentPrototype>().Take(128).OrderBy(value => value.ID, StringComparer.Ordinal))
                {
                    if (!department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(job))) continue;
                    result.Add((NCPersistentIdentity.Organization(department.ID).ToString(), true));
                    if (result.Count == 16) break;
                }
            return result.Distinct().ToArray();
        });
    }

    public async Task<NCMetaSoftwareProductAccess?> ReadProductAccessAsync(EntityUid terminal, EntityUid user,
        Guid productId, int major, Guid releaseId, string? owner = null)
    {
        if (productId == Guid.Empty || major < 1) return null;
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return null;
        owner ??= context.Actor;
        if (!await CanActFor(context, owner)) return null;
        var product = await ReadProductAsync(productId, major);
        if (releaseId == Guid.Empty && product?.Releases.LastOrDefault() is { } latest) releaseId = latest.Id;
        if (product == null || !product.Releases.Any(value => value.Id == releaseId)) return null;
        var license = await ReadProductLicense(owner, context.DeviceId, productId, major, context.Time.Ticks);
        var canPublish = product.Publisher == owner && await _persistence.ReadProjectAsync(product.ProjectId, owner) != null;
        var paths = Array.Empty<string>();
        if (MetaSoftwarePolicy.CanReadSource(product, license, owner, context.Time.Ticks))
        {
            var publication = await Read<NCMetaSoftwarePublication>(PublicationType, Key(productId, major) + ":" + releaseId.ToString("D"));
            paths = publication?.Release.Sources?.Select(value => value.Path).Order(StringComparer.Ordinal).ToArray() ?? [];
        }
        if (!await CanActFor(context, owner)) return null;
        // Refresh the selected entitlement after I/O, so a concurrent transfer cannot disclose protected source paths.
        if (license != null) license = await Read<MetaSoftwareLicense>(LicenseType, license.Id.ToString("D"));
        var now = await Main(() => Current(context) && _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1);
        if (now < 0) return null;
        if (!IsCurrentProductLicense(license, owner, productId, major, now)) license = null;
        if (!MetaSoftwarePolicy.CanReadSource(product, license, owner, now)) paths = [];
        return new(product, license, canPublish, paths);
    }

    private static bool IsCurrentProductLicense(MetaSoftwareLicense? license, string owner, Guid product, int major, long now) =>
        license is { Revoked: false } && license.Owner == owner && license.ProductId == product && license.Major == major &&
        (license.IllegalExpiresAtTicks == null || now < license.IllegalExpiresAtTicks);

    private async Task<MetaSoftwareLicense?> ReadProductLicense(string owner, string deviceId, Guid product, int major, long now)
    {
        var key = Key(product, major);
        var device = await Read<NCMetaSoftwareDeviceIndex>(DeviceType, deviceId);
        if (device?.Licenses.TryGetValue(key, out var attachedId) == true)
        {
            var attached = await Read<MetaSoftwareLicense>(LicenseType, attachedId.ToString("D"));
            if (IsCurrentProductLicense(attached, owner, product, major, now)) return attached;
        }
        var index = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, owner);
        if (index == null) return null;
        // New snapshots have a direct product bucket; the bounded legacy fallback needs no repeated UI/rate-limit calls.
        var candidates = index.ProductLicenses.TryGetValue(key, out var selected) ? selected : index.Licenses;
        foreach (var group in candidates.Reverse().Chunk(8))
        {
            var values = await Task.WhenAll(group.Select(id => Read<MetaSoftwareLicense>(LicenseType, id.ToString("D"))));
            var value = values.FirstOrDefault(license => IsCurrentProductLicense(license, owner, product, major, now));
            if (value != null) return value;
        }
        return null;
    }

    private static NCMetaSoftwareOwnerIndex AddOwnerLicense(NCMetaSoftwareOwnerIndex index, MetaSoftwareLicense license)
    {
        var buckets = new Dictionary<string, Guid[]>(index.ProductLicenses, StringComparer.Ordinal);
        var key = Key(license.ProductId, license.Major);
        buckets[key] = [.. buckets.GetValueOrDefault(key) ?? [], license.Id];
        return index with { Licenses = [.. index.Licenses, license.Id], ProductLicenses = buckets, Revision = checked(index.Revision + 1) };
    }
    private static NCMetaSoftwareOwnerIndex RemoveOwnerLicense(NCMetaSoftwareOwnerIndex index, MetaSoftwareLicense license)
    {
        var buckets = new Dictionary<string, Guid[]>(index.ProductLicenses, StringComparer.Ordinal);
        var key = Key(license.ProductId, license.Major);
        if (buckets.TryGetValue(key, out var previous))
        {
            var retained = previous.Where(id => id != license.Id).ToArray();
            if (retained.Length == 0) buckets.Remove(key); else buckets[key] = retained;
        }
        return index with { Licenses = index.Licenses.Where(id => id != license.Id).ToArray(), ProductLicenses = buckets, Revision = checked(index.Revision + 1) };
    }

    public async Task<MetaSoftwareProduct[]> ReadCatalogPageAsync(EntityUid terminal, EntityUid user, int offset = 0, int limit = 8)
    {
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return [];
        var products = await ReadCatalogPageAsync(offset, limit);
        return await Main(() => Current(context)) ? products : [];
    }
    /// <summary>Public descriptors only. Callers project the selected page into bounded native UI summaries.</summary>
    public async Task<MetaSoftwareProduct[]> ReadCatalogPageAsync(int offset = 0, int limit = 8)
    {
        if (offset is < 0 or > 256 || limit is < 1 or > 8) return [];
        var catalog = await Read<NCMetaSoftwareCatalog>("meta.software.catalog", "public");
        if (catalog == null) return [];
        var result = new List<MetaSoftwareProduct>();
        foreach (var entry in catalog.Products.Skip(offset).Take(limit))
        {
            var product = await ReadProductAsync(entry.ProductId, entry.Major);
            if (product is { Offered: true }) result.Add(product);
        }
        return result.ToArray();
    }

    /// <summary>Private entitlement projection. Never expose activation authority epochs in a client DTO.</summary>
    public async Task<MetaSoftwareLicense[]> ReadLicensePageAsync(EntityUid terminal, EntityUid user,
        string? owner = null, int offset = 0, int limit = 8)
    {
        if (offset is < 0 or > 128 || limit is < 1 or > 8) return [];
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return [];
        owner ??= context.Actor;
        if (!await CanActFor(context, owner)) return [];
        var index = await Read<NCMetaSoftwareOwnerIndex>(OwnerType, owner);
        if (index == null) return [];
        var result = new List<MetaSoftwareLicense>();
        foreach (var id in index.Licenses.Skip(offset).Take(limit))
        {
            var license = await Read<MetaSoftwareLicense>(LicenseType, id.ToString("D"));
            if (license?.Owner == owner) result.Add(license);
        }
        return await CanActFor(context, owner) ? result.ToArray() : [];
    }
}
