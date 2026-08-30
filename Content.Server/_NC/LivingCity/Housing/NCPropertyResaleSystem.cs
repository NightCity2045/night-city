// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Owner-facing resale boundary. Prices remain server requests and ownership still changes only in the atomic market
/// settlement; the listing itself never mutates the property aggregate.
/// </summary>
public sealed partial class NCPropertyResaleSystem : EntitySystem
{
    [Dependency] private NCPropertyRegistrySystem _properties = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _market = default!;

    public async Task<NCPropertyMarketMutationResult> CreateAsync(
        NCApartmentId propertyId,
        NCPartyRef seller,
        long price,
        Guid correlationId)
    {
        if (!propertyId.IsValid || !seller.IsValid || price <= 0 || correlationId == Guid.Empty)
            return Reject("The resale request is invalid.");
        var property = await _properties.GetAsync(propertyId, true);
        if (property == null || property.Snapshot.Owner != seller ||
            property.Snapshot.LegalState != NCPropertyLegalState.Clear ||
            property.Snapshot.Status is not (NCPropertyStatus.Available or NCPropertyStatus.OwnerOccupied or
                NCPropertyStatus.ResaleListed) ||
            property.Snapshot.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Tenancy))
            return Reject("The seller does not own an unencumbered saleable property.");
        var stable = $"resale.{property.AggregateVersion}.{price}";
        var listingId = NCPersistentIdentity.PropertyListing(propertyId, stable);
        return await _market.CreateListingAsync(new NCCreatePropertyListingRequest(
            NCPersistentIdentity.Command("housing", "resale-listing", $"{propertyId}.{stable}"), listingId,
            stable, propertyId, property.AggregateVersion, NCPropertyOfferKind.Sale, price, 0,
            string.Empty, 0, null, null, NCPropertyPermission.All, null, seller, correlationId,
            property.Snapshot.LastAppliedEventId, "housing-owner-resale"));
    }

    private static NCPropertyMarketMutationResult Reject(string error) =>
        new(NCPropertyAggregateMutationStatus.Rejected, null, null, null, error);
}
