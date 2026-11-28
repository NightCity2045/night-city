// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Pawn;

[Serializable, NetSerializable]
public enum NCPawnUiKey : byte
{
    Key,
}

/// <summary>One thing the seller holds: the item in hand or something inside it. Payout 0 means refused.</summary>
[Serializable, NetSerializable]
public sealed record NCPawnLine(NetEntity Item, string Prototype, string Name, int Count, int Payout, bool Container);

/// <summary>Per-seller view: each player sees what they themselves hold.</summary>
[Serializable, NetSerializable]
public sealed class NCPawnUpdateMessage(string shop, List<NCPawnLine> lines) : BoundUserInterfaceMessage
{
    public readonly string Shop = shop;
    public readonly List<NCPawnLine> Lines = lines;
}

[Serializable, NetSerializable]
public sealed class NCPawnSellMessage(NetEntity item) : BoundUserInterfaceMessage
{
    public readonly NetEntity Item = item;
}

[Serializable, NetSerializable]
public sealed class NCPawnSellAllMessage : BoundUserInterfaceMessage;

/// <summary>One sale as the buyer remembers it this round.</summary>
[Serializable, NetSerializable]
public sealed record NCPawnLogEntry(TimeSpan Time, string Shop, string Seller, string Item, int Count, int Payout);

/// <summary>Sent on the NCPD records console: asks for the round's pawn log of every buyer.</summary>
[Serializable, NetSerializable]
public sealed class NCPolicePawnLogRequestMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCPolicePawnLogMessage(List<NCPawnLogEntry> entries) : BoundUserInterfaceMessage
{
    public readonly List<NCPawnLogEntry> Entries = entries;
}
