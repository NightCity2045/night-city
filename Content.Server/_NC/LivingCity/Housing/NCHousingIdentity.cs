// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>Stable identities owned by the canonical Housing domain.</summary>
public static class NCHousingIdentity
{
    public static NCPartyRef HousingService => NCPersistentIdentity.CityService("night-city-housing-service");

    public static Guid Command(string operation, NCApartmentId propertyId) =>
        NCPersistentIdentity.Command("housing", operation, propertyId.ToString());

    public static Guid Correlation(string operation, NCApartmentId propertyId) =>
        NCPersistentIdentity.Correlation("housing", operation, propertyId.ToString());
}
