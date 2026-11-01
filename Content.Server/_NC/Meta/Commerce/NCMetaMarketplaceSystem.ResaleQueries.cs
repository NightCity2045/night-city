// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private const string ResaleRecipientsType = "meta.software.resale-recipient";
    [Dependency] private NCPartyResolverSystem _softwareParties = default!;

    public async Task<NCMetaSoftwareResaleListing[]> ReadResaleOffersAsync(EntityUid terminal, EntityUid user,
        string? owner = null, int offset = 0, int limit = 8)
    {
        if (offset is < 0 or > 128 || limit is < 1 or > 8) return [];
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return [];
        owner ??= context.Actor;
        if (!await CanActFor(context, owner)) return [];
        var index = await Read<NCMetaSoftwareResaleRecipientIndex>(ResaleRecipientsType, owner);
        if (index == null) return [];
        var active = await ReadActiveRecipientOffers(index, context.Time.Ticks);
        var result = new List<NCMetaSoftwareResaleListing>();
        foreach (var offer in active.Skip(offset).Take(limit))
        {
            var license = await Read<MetaSoftwareLicense>(LicenseType, offer.LicenseId.ToString("D"));
            if (license == null || license.Owner != offer.Seller || license.Revision != offer.LicenseRevision || !MetaSoftwarePolicy.CanResell(license)) continue;
            var product = await ReadProductAsync(license.ProductId, license.Major);
            if (product != null) result.Add(new(offer, product, license.Terms));
        }
        return await CanActFor(context, owner) ? result.ToArray() : [];
    }

    private async Task<NCMetaSoftwareResaleOffer[]> ReadActiveRecipientOffers(NCMetaSoftwareResaleRecipientIndex index, long now)
    {
        var result = new List<NCMetaSoftwareResaleOffer>();
        foreach (var group in index.Licenses.Chunk(8))
        {
            var offers = await Task.WhenAll(group.Select(id => Read<NCMetaSoftwareResaleOffer>("meta.software.resale", id.ToString("D"))));
            result.AddRange(offers.Where(value => value != null && value.Buyer == index.Owner && !value.Consumed && now < value.ExpiresAtTicks).Select(value => value!));
        }
        return result.OrderBy(value => value.ExpiresAtTicks).ThenBy(value => value.LicenseId).ToArray();
    }

    /// <summary>Uses the existing public personnel directory and real organization records; UUID text is not a contact selector.</summary>
    public async Task<NCMetaSoftwareRecipient[]> SearchResaleRecipientsAsync(EntityUid terminal, EntityUid user, string query, int limit = 8)
    {
        if (query is not { Length: <= 64 } || limit is < 1 or > 8) return [];
        var context = await Main(() => Capture(terminal, user));
        if (context == null) return [];
        var found = await _database.SearchNCPublicPersonnelAsync(query.Trim(), limit);
        var result = found.Select(value => new NCMetaSoftwareRecipient(NCPartyRef.ForCharacter(value.CharacterId).ToString(), value.CharacterName, false)).ToList();
        var organizations = await Main(() => _prototypes.EnumeratePrototypes<DepartmentPrototype>().Take(128)
            .Select(value => (Party: NCPersistentIdentity.Organization(value.ID), Label: Loc.GetString(value.Name)))
            .Where(value => value.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(value => value.Label, StringComparer.Ordinal).Take(limit).ToArray());
        foreach (var organization in organizations)
        {
            var resolved = await _softwareParties.ResolveAsync(organization.Party, true);
            if (resolved.IsResolved) result.Add(new(organization.Party.ToString(), resolved.DisplayName, true));
        }
        return await Main(() => Current(context)) ? result.Where(value => value.Party != context.Actor).DistinctBy(value => value.Party)
            .OrderBy(value => value.DisplayName, StringComparer.Ordinal).Take(limit).ToArray() : [];
    }
}
