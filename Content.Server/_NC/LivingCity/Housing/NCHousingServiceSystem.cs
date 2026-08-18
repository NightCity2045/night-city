// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Ensures the city housing service exists as an ordinary persistent party with an ordinary ledger account.
/// </summary>
public sealed partial class NCHousingServiceSystem : EntitySystem
{
    private const string DisplayName = "Night City Housing Service";

    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;

    public NCPartyRef Party => NCHousingIdentity.HousingService;

    public async Task<bool> EnsureReadyAsync(string auditSource)
    {
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("housing", "register-service", "night-city-housing-service"),
            Party,
            DisplayName,
            NCPersistentIdentity.Correlation("housing", "register-service", "night-city-housing-service"),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate))
        {
            var existing = await _directory.GetAsync(Party, true);
            if (existing == null || existing.Snapshot.Lifecycle != NCPartyLifecycle.Active)
                return false;
        }

        return (await _finance.EnsurePrimaryAccountAsync(Party, auditSource)).IsResolved;
    }
}
