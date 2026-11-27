// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Fixers;

[Serializable, NetSerializable]
public enum NCFixerUiKey : byte
{
    Key,
}

public enum NCFixerMissionStatus : byte
{
    Active,
    AwaitingPayment,
}

/// <summary>Where something of a mission is: district and building from the city atlas, and the floor.</summary>
[Serializable, NetSerializable]
public sealed record NCMissionPlace(string? District, string? Building, int Floor, ProtoId<NCMissionOwnerPrototype>? Owner);

[Serializable, NetSerializable]
public sealed record NCFixerOffer(int Slot, ProtoId<NCFixerMissionPrototype> Mission, bool Locked);

[Serializable, NetSerializable]
public sealed record NCFixerActiveMission(
    ProtoId<NCFixerMissionPrototype> Mission,
    ProtoId<NCFixerPrototype> Fixer,
    NCFixerMissionStatus Status,
    TimeSpan TimeLeft,
    NCMissionPlace? Objective,
    NCMissionPlace? Drop,
    string? TargetName);

[Serializable, NetSerializable]
public sealed class NCFixerUiState(
    ProtoId<NCFixerPrototype> fixer,
    int greeting,
    int reputation,
    int level,
    List<NCFixerOffer> offers,
    NCFixerActiveMission? active,
    bool activeHere) : BoundUserInterfaceState
{
    public readonly ProtoId<NCFixerPrototype> Fixer = fixer;
    public readonly int Greeting = greeting;
    public readonly int Reputation = reputation;
    public readonly int Level = level;
    public readonly List<NCFixerOffer> Offers = offers;
    public readonly NCFixerActiveMission? Active = active;

    /// <summary>The active mission was given by this fixer, so it can be abandoned or paid out here.</summary>
    public readonly bool ActiveHere = activeHere;
}

[Serializable, NetSerializable]
public sealed class NCFixerAcceptMessage(int slot) : BoundUserInterfaceMessage
{
    public readonly int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class NCFixerAbandonMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCFixerCollectPaymentMessage : BoundUserInterfaceMessage;
