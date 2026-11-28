// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Materials;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Pawn;

/// <summary>
/// Street values the buyers work from. An item's value is, in order: an explicit price, a share of the retail price
/// when the city's shops sell it, the material value for printed goods, the first matching category, else junk.
/// Each buyer then pays its own share of that value.
/// </summary>
[Prototype("ncPawnPrices")]
public sealed partial class NCPawnPricesPrototype : IPrototype
{
    public const string DefaultId = "NCPawnPrices";

    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Exact street values that override everything else (mission goods and the like).</summary>
    [DataField]
    public Dictionary<EntProtoId, int> Items = new();

    /// <summary>Street value of goods the city's shops sell, as a share of their retail price.</summary>
    [DataField]
    public float RetailShare = 0.9f;

    /// <summary>Street value of one sheet (100 units) of each material; printed goods are worth their materials.</summary>
    [DataField]
    public Dictionary<ProtoId<MaterialPrototype>, int> Materials = new();

    [DataField]
    public int MaterialFallback = 3;

    /// <summary>Printed goods are worth their materials plus this much for the work.</summary>
    [DataField]
    public float PrintedMarkup = 1.1f;

    /// <summary>Checked in order; the first category the item matches sets its value.</summary>
    [DataField]
    public List<NCPawnCategory> Categories = new();

    /// <summary>Value of anything that matches nothing else.</summary>
    [DataField]
    public int Junk = 2;

    /// <summary>Items carrying any of these components are never bought (cash, IDs, keys, bodies).</summary>
    [DataField]
    public List<string> RefusedComponents = new();

    [DataField]
    public List<ProtoId<StackPrototype>> RefusedStacks = new();
}

[DataDefinition]
public sealed partial class NCPawnCategory
{
    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public int Price;

    /// <summary>Matches when the item descends from any of these prototypes.</summary>
    [DataField]
    public List<EntProtoId> Parents = new();

    /// <summary>Matches when the item has any of these components.</summary>
    [DataField]
    public List<string> Components = new();
}
