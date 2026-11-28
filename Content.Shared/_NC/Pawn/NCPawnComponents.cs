// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Research.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Pawn;

/// <summary>
/// An NPC buyer. Sellers pile goods on the buyer's pallet, talk to the buyer and sell; cash is handed over in person.
/// </summary>
[RegisterComponent]
public sealed partial class NCPawnShopComponent : Component
{
    /// <summary>Pallets within this range of the buyer are its own.</summary>
    [DataField]
    public float PalletRange = 2.5f;

    /// <summary>The buyer's name in the NCPD pawn log.</summary>
    [DataField(required: true)]
    public LocId Name;

    /// <summary>Share of the street value paid out.</summary>
    [DataField(required: true)]
    public float Payout;

    [DataField]
    public ProtoId<NCPawnPricesPrototype> Prices = NCPawnPricesPrototype.DefaultId;
}

/// <summary>A buyer's pallet: whatever lies on it (crates and bags with their contents) is offered for sale.</summary>
[RegisterComponent]
public sealed partial class NCPawnPalletComponent : Component;

/// <summary>Hidden mark on everything a lathe or fabricator prints: buyers value it by its materials.</summary>
[RegisterComponent]
public sealed partial class NCPrintedItemComponent : Component
{
    [DataField]
    public ProtoId<LatheRecipePrototype>? Recipe;
}

/// <summary>Broadcast for an item a lathe has just produced, before it merges into a nearby stack.</summary>
[ByRefEvent]
public readonly record struct NCLatheProducedEvent(EntityUid Item, EntityUid Lathe, ProtoId<LatheRecipePrototype> Recipe);
