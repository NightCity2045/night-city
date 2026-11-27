// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Housing;

/// <summary>Terms and prices of the real estate agency. The prototype with id Default is used.</summary>
[Prototype("ncRealEstate")]
public sealed partial class NCRealEstatePrototype : IPrototype
{
    public const string DefaultId = "Default";

    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Prepayment terms on offer, in hours of active world time.</summary>
    [DataField(required: true)]
    public List<int> Terms = new();

    /// <summary>Price of one duplicate key card.</summary>
    [DataField]
    public int CardPrice = 50;

    /// <summary>Price of changing the locks: every card stops working and a fresh set is cut.</summary>
    [DataField]
    public int RekeyPrice = 250;

    /// <summary>Warning sent to the tenant before the prepaid time runs out.</summary>
    [DataField]
    public TimeSpan WarningTime = TimeSpan.FromMinutes(15);
}
