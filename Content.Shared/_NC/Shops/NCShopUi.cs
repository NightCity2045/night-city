// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Shops;

[Serializable, NetSerializable]
public enum NCShopUiKey : byte
{
    Key,
}

/// <summary>One line of the shop: catalog index, item prototype, price and what is left this round.</summary>
[Serializable, NetSerializable]
public sealed record NCShopLine(int Index, string Item, int Price, int Stock);

[Serializable, NetSerializable]
public sealed class NCShopUiState(string catalog, bool hasAccount, long balance, List<NCShopLine> lines) : BoundUserInterfaceState
{
    /// <summary>The catalog prototype, for its name.</summary>
    public readonly string Catalog = catalog;
    public readonly bool HasAccount = hasAccount;
    public readonly long Balance = balance;
    public readonly List<NCShopLine> Lines = lines;
}

[Serializable, NetSerializable]
public sealed class NCShopBuyMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}
