// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Cargo.Pawn;

[Serializable, NetSerializable]
public enum NCPawnShopUiKey : byte { Key }

[Serializable, NetSerializable]
public sealed record NCPawnShopListing(string Name, long Price);

[Serializable, NetSerializable]
public sealed class NCPawnShopState(int percentage, long total, List<NCPawnShopListing> listings,
    Guid quoteId, string notice, bool canSell) : BoundUserInterfaceState
{
    public readonly int Percentage = percentage;
    public readonly long Total = total;
    public readonly List<NCPawnShopListing> Listings = listings;
    public readonly Guid QuoteId = quoteId;
    public readonly string Notice = notice;
    public readonly bool CanSell = canSell;
}

[Serializable, NetSerializable]
public sealed class NCPawnShopAppraiseMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCPawnShopSellMessage(Guid quoteId) : BoundUserInterfaceMessage
{
    public readonly Guid QuoteId = quoteId;
}
