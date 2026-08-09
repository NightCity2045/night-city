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
}

[Flags]
public enum NCPropertyPermission : ushort
{
    None = 0,
    Enter = 1 << 0,
    ManageCards = 1 << 1,
    ManageTenants = 1 << 2,
    OrderInterior = 1 << 3,
    // Legacy database/card bit retained for compatibility; player rental flows never grant it.
    Sell = 1 << 4,
    All = Enter | ManageCards | ManageTenants | OrderInterior,
}

public enum NCPropertyOccupancyKind : byte
{
    Ownership,
    Tenancy,
}

public enum NCPropertyReleaseReason : byte
{
    Sale,
    VoluntaryMoveOut,
    Eviction,
    LegalEvent,
    Administrative,
}
