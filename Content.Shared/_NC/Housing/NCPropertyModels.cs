// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Housing;

public enum NCPropertyStatus : byte
{
    Unavailable,
    Available,
    Reserved,
    OwnerOccupied,
    Rented,
    Delinquent,
    EvictionPending,
    ResaleListed,
    LegalHold,
    Condemned,
    Destroyed,
}

[Flags]
public enum NCPropertyPermission : ushort
{
    None = 0,
    Enter = 1 << 0,
    ManageCards = 1 << 1,
    ManageTenants = 1 << 2,
    OrderInterior = 1 << 3,
    StoreItems = 1 << 4,
    Renovate = 1 << 5,
    Transfer = 1 << 6,
    All = Enter | ManageCards | ManageTenants | OrderInterior | StoreItems | Renovate | Transfer,
}

public enum NCPropertyOccupancyKind : byte
{
    Ownership,
    Tenancy,
    Resident,
    Guest,
}

public enum NCPropertyLegalState : byte
{
    Clear,
    Hold,
    Condemned,
    Destroyed,
}

public enum NCPropertyAssetClass : byte
{
    Fixture,
    IncludedAsset,
    PersonalProperty,
}

public enum NCPropertyReleaseReason : byte
{
    Sale,
    VoluntaryMoveOut,
    Eviction,
    LegalEvent,
    Administrative,
}

public enum NCPropertyMutationResult : byte
{
    Success,
    NotFound,
    CharacterNotFound,
    BankAccountNotFound,
    InsufficientFunds,
    NotAvailable,
    NotAuthorized,
    Conflict,
    InvalidRequest,
}
