// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;

namespace Content.Server._NC.City.Connectivity;

public enum NCCityConnectivityIssueSeverity : byte
{
    Warning,
    Error,
}

public enum NCCityConnectivityIssueCode : byte
{
    MissingNavigationRoom,
    NoTraversableExit,
    IsolatedStreet,
    UnreachableFromStreet,
    MissingSpawnCandidate,
    InvalidPortalEndpoint,
    CrossNetworkPortal,
    InvalidPortalCost,
    InvalidStablePortalKind,
    InvalidValidationRules,
}

public readonly record struct NCCityConnectivityValidationIssue(
    NCCityConnectivityIssueSeverity Severity,
    NCCityConnectivityIssueCode Code,
    NCZoneId ZoneId,
    string Message);

public readonly record struct NCCityConnectivityValidationSummary(
    long ValidationVersion,
    long CityGraphVersion,
    long RoomGraphVersion,
    long SpawnIndexVersion,
    int Errors,
    int Warnings);
