// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Housing.UI;

[Serializable, NetSerializable]
public enum NCPropertyConsoleUiKey : byte
{
    Key,
}

/// <summary>
/// One server-filtered property entry. Authority is never inferred from these client-facing flags.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCPropertyConsoleInteriorOfferSummary(
    string TemplateId,
    int RentAmount,
    long BillingIntervalSeconds,
    long GracePeriodSeconds);

[Serializable, NetSerializable]
public sealed record NCPropertyConsoleListingSummary(
    string ListingId,
    NCApartmentId ApartmentId,
    string ApartmentName,
    string BuildingName,
    NCPropertyStatus Status,
    int RentAmount,
    long BillingIntervalSeconds,
    long GracePeriodSeconds,
    long? NextRentDueUnixSeconds,
    bool IsTenant,
    bool CanRent,
    bool CanEndLease,
    bool CanInvokeInterior,
    bool InteriorActive,
    string? CurrentInterior,
    List<NCPropertyConsoleInteriorOfferSummary> Interiors);

/// <summary>
/// Sent to one user only so character balance, ownership and selection cannot leak between terminal users.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPropertyConsoleUpdateMessage(
    List<NCPropertyConsoleListingSummary> listings,
    string? selectedListingId,
    int pageIndex,
    int pageCount,
    int totalCount,
    int balance,
    bool hasBankAccount,
    string notice) : BoundUserInterfaceMessage
{
    public readonly List<NCPropertyConsoleListingSummary> Listings = listings;
    public readonly string? SelectedListingId = selectedListingId;
    public readonly int PageIndex = pageIndex;
    public readonly int PageCount = pageCount;
    public readonly int TotalCount = totalCount;
    public readonly int Balance = balance;
    public readonly bool HasBankAccount = hasBankAccount;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsoleSearchMessage(string query) : BoundUserInterfaceMessage
{
    public readonly string Query = query;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsolePageMessage(int pageIndex) : BoundUserInterfaceMessage
{
    public readonly int PageIndex = pageIndex;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsoleSelectMessage(string listingId) : BoundUserInterfaceMessage
{
    public readonly string ListingId = listingId;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsoleRentMessage(string listingId, string templateId) : BoundUserInterfaceMessage
{
    public readonly string ListingId = listingId;
    public readonly string TemplateId = templateId;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsoleEndLeaseMessage(string listingId) : BoundUserInterfaceMessage
{
    public readonly string ListingId = listingId;
}

[Serializable, NetSerializable]
public sealed class NCPropertyConsoleInvokeInteriorMessage(string listingId) : BoundUserInterfaceMessage
{
    public readonly string ListingId = listingId;
}
