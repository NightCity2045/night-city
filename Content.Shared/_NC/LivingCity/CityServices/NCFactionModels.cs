// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Shared._NC.LivingCity.CityServices;

public enum NCFactionRelationStatus : byte { Allied, Friendly, Neutral, Rival, Truce, War }

public sealed record NCFactionPolicy(int TaxBasisPoints, int ProtectionBasisPoints, int PatrolPriority,
    int SupplyPriority, bool EnforcesLaw, bool UsesCovertOperations);
public sealed record NCFactionTerritoryStake(NCZoneId ZoneId, int Influence, bool ClaimsControl,
    NCActiveWorldTime UpdatedAt);
public sealed record NCFactionRelation(NCOrganizationId OtherFactionId, NCFactionRelationStatus Status,
    int Trust, int Hostility, NCActiveWorldTime? ExpiresAt);
public sealed record NCFactionResources(NCAccountId AccountId, IReadOnlyList<NCStockNodeId> StockNodes,
    int AvailableManpower, int ReservedManpower, IReadOnlyList<NCSocialObligationId> Obligations);

public sealed record NCFactionSnapshot(NCOrganizationId FactionId, NCPartyRef Party, string StableKey,
    string DisplayName, IReadOnlyList<string> IdeologyKeys, NCFactionPolicy Policy,
    IReadOnlyList<NCFactionTerritoryStake> Territories, IReadOnlyList<NCFactionRelation> Relations,
    NCFactionResources Resources, int DataVersion, Guid LastAppliedEventId);

public sealed record NCTerritoryControlProjection(NCZoneId ZoneId, NCOrganizationId? Controller,
    IReadOnlyList<NCFactionTerritoryStake> Contenders, int TaxBasisPoints, int ProtectionBasisPoints,
    int PatrolPressure, int SupplyPriority, long Revision);
