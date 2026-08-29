// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Content.Shared._NC.LivingCity.Affordances;

namespace Content.Shared._NC.LivingCity.Fixers;

[Prototype("ncFixerPolicy")]
public sealed partial class NCFixerPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumContacts { get; private set; } = 512;
    [DataField] public int MaximumDealLegs { get; private set; } = 16;
    [DataField] public int MaximumEvidence { get; private set; } = 32;
    [DataField] public int MaximumActiveDealsPerNetwork { get; private set; } = 256;
    [DataField] public TimeSpan QuoteLifetime { get; private set; } = TimeSpan.FromMinutes(15);
    [DataField] public TimeSpan ReservationLifetime { get; private set; } = TimeSpan.FromMinutes(10);
    [DataField] public int MaximumBrokerFeeBasisPoints { get; private set; } = 2_500;
    [DataField] public int MaximumRiskBasisPoints { get; private set; } = 10_000;
    [DataField] public int MaximumLifecycleAdvancesPerTick { get; private set; } = 8;
}

[Prototype("ncNightMarketPolicy")]
public sealed partial class NCNightMarketPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumVendors { get; private set; } = 64;
    [DataField] public TimeSpan MinimumAssemblyLead { get; private set; } = TimeSpan.FromMinutes(10);
    [DataField] public TimeSpan MaximumDuration { get; private set; } = TimeSpan.FromHours(8);
    [DataField] public int MaximumIncidents { get; private set; } = 64;
    /// <summary>Existing authored building purposes where a physical Night Market may assemble.</summary>
    [DataField] public List<NCCityAffordanceKind> AllowedVenueAffordances { get; private set; } =
        [NCCityAffordanceKind.Shop, NCCityAffordanceKind.Warehouse];
    [DataField] public int AttendancePriority { get; private set; } = 700;
}
