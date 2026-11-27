// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Corporations;

/// <summary>A corporation sales console: shows the open contracts and fulfils them from nearby sales pallets.</summary>
[RegisterComponent]
public sealed partial class NCCorpSalesConsoleComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCCorpSalesPrototype> Corporation;

    /// <summary>Sales pallets within this many tiles of the console are used.</summary>
    [DataField]
    public float PalletRange = 2.5f;
}

/// <summary>Goods placed on this pallet, loose or inside crates, count toward contracts.</summary>
[RegisterComponent]
public sealed partial class NCCorpSalesPalletComponent : Component;
