// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;
using Content.Shared._NC.LivingCity.Economy;

namespace Content.Shared._NC.LivingCity.Economy.Shop;

[Serializable, NetSerializable]
public enum NCEconomicShopUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record NCEconomicShopListingSummary(
    NCEconomicOfferKind Kind,
    string AssetId,
    string DefinitionId,
    string DisplayName,
    string Description,
    string Category,
    string PreviewPrototype,
    long AvailableQuantity,
    long BaseUnitPrice,
    int QualityOrCondition,
    TimeSpan? ShelfLifeRemaining);

[Serializable, NetSerializable]
public sealed class NCEconomicShopState(
    string shopName,
    long balance,
    bool hasAccount,
    bool ready,
    string notice,
    List<NCEconomicShopListingSummary> listings) : BoundUserInterfaceState
{
    public readonly string ShopName = shopName;
    public readonly long Balance = balance;
    public readonly bool HasAccount = hasAccount;
    public readonly bool Ready = ready;
    public readonly string Notice = notice;
    public readonly List<NCEconomicShopListingSummary> Listings = listings;
}

[Serializable, NetSerializable]
public sealed class NCEconomicShopPurchaseMessage(
    NCEconomicOfferKind kind,
    string assetId,
    long quantity) : BoundUserInterfaceMessage
{
    public readonly NCEconomicOfferKind Kind = kind;
    public readonly string AssetId = assetId;
    public readonly long Quantity = quantity;
}
