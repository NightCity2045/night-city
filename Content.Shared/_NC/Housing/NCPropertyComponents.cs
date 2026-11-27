// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared._NC.Housing;

/// <summary>
/// Mapping marker placed inside a rentable property. The property is the floor reachable from the marker up to
/// walls and doors, plus back rooms whose only doors open into it. Several markers with one id join their areas.
/// </summary>
[RegisterComponent]
public sealed partial class NCPropertyMarkerComponent : Component
{
    /// <summary>Set by the mapper on each placed marker; markers without one are ignored.</summary>
    [DataField]
    public string Id = string.Empty;

    /// <summary>What the property is called on the market, e.g. "Apt 3B". Map data, like building names.</summary>
    [DataField]
    public string? Name;

    /// <summary>Rent per hour of active world time.</summary>
    [DataField]
    public int RentPerHour;
}

/// <summary>A plastic key card for one door of a rented property. Changing the locks voids it.</summary>
[RegisterComponent]
public sealed partial class NCPropertyCardComponent : Component
{
    [DataField]
    public string PropertyId = string.Empty;

    /// <summary>Which door of the property it opens; stable between rounds.</summary>
    [DataField]
    public string DoorKey = string.Empty;

    [DataField]
    public int KeyVersion;
}

/// <summary>A real estate agency terminal: the market of rentable properties.</summary>
[RegisterComponent]
public sealed partial class NCRealEstateTerminalComponent : Component;
