// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Shops;

/// <summary>What a shop vending machine sells: items, prices and how many of each it holds per round.</summary>
[Prototype("ncShopCatalog")]
public sealed partial class NCShopCatalogPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public List<NCShopEntry> Entries = new();
}

[DataDefinition]
public sealed partial class NCShopEntry
{
    [DataField(required: true)]
    public EntProtoId Item;

    [DataField(required: true)]
    public int Price;

    /// <summary>How many the machine holds at the start of every round.</summary>
    [DataField]
    public int Stock = 5;
}
