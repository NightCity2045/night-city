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

    /// <summary>Someone else's lease offered for resale.</summary>
    ForSale,
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
    TimeSpan Remaining,
    long ResalePrice,
    bool ResaleReserved,
    string ResaleBuyer,
    bool Home);

[Serializable, NetSerializable]
public sealed class NCRealEstateUiState(
    bool open,
    bool hasAccount,
    long balance,
    List<int> terms,
    int cardPrice,
    int rekeyPrice,
    List<NCPropertyListing> properties) : BoundUserInterfaceState
{
    /// <summary>False while the world clock or the lease records are not ready.</summary>
    public readonly bool Open = open;
    public readonly bool HasAccount = hasAccount;
    public readonly long Balance = balance;

    /// <summary>The prepayment terms on offer, in hours.</summary>
    public readonly List<int> Terms = terms;
    public readonly int CardPrice = cardPrice;
    public readonly int RekeyPrice = rekeyPrice;
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

/// <summary>A duplicate card for one door (0-based) of the user's own lease.</summary>
[Serializable, NetSerializable]
public sealed class NCRealEstateDuplicateMessage(string property, int door) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
    public readonly int Door = door;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateRekeyMessage(string property) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
}

/// <summary>Offers the user's lease for resale; an empty buyer means anyone may buy.</summary>
[Serializable, NetSerializable]
public sealed class NCRealEstateListMessage(string property, long price, string buyer) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
    public readonly long Price = price;
    public readonly string Buyer = buyer;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateUnlistMessage(string property) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
}

[Serializable, NetSerializable]
public sealed class NCRealEstateBuyMessage(string property) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
}

/// <summary>Marks the user's own lease as the place they enter the round, or clears it.</summary>
[Serializable, NetSerializable]
public sealed class NCRealEstateSetHomeMessage(string property, bool home) : BoundUserInterfaceMessage
{
    public readonly string Property = property;
    public readonly bool Home = home;
}
