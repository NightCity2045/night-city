// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Housing;

[Serializable, NetSerializable]
public enum NCRealEstateUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NCPropertyStatus : byte
{
    Vacant,
    Rented,
    Mine,
}

/// <summary>One property on the agency's list.</summary>
[Serializable, NetSerializable]
public sealed record NCPropertyListing(
    string Id,
    string Name,
    string District,
    string Building,
    int Floor,
    int Area,
    int Doors,
    int RentPerHour,
    NCPropertyStatus Status,
    TimeSpan Remaining);

[Serializable, NetSerializable]
public sealed class NCRealEstateUiState(
    bool open,
    bool hasAccount,
    long balance,
    List<int> terms,
    List<NCPropertyListing> properties) : BoundUserInterfaceState
{
    /// <summary>False while the world clock or the lease records are not ready.</summary>
    public readonly bool Open = open;
    public readonly bool HasAccount = hasAccount;
    public readonly long Balance = balance;

    /// <summary>The prepayment terms on offer, in hours.</summary>
    public readonly List<int> Terms = terms;
    public readonly List<NCPropertyListing> Properties = properties;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateRentMessage(string property, int hours) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
    public readonly int Hours = hours;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateExtendMessage(string property, int hours) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
    public readonly int Hours = hours;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateCancelMessage(string property) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
}
