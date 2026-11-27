// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Shops;

/// <summary>
/// A shop vending machine: sells its catalog for money taken straight from the buyer's bank account. The stock
/// refills every round.
/// </summary>
[RegisterComponent]
public sealed partial class NCShopComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCShopCatalogPrototype> Catalog;
}

/// <summary>Mapping marker where a nearby shop puts goods too big to hand over, like furniture.</summary>
[RegisterComponent]
public sealed partial class NCShopPickupPointComponent : Component;
