// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Corporations;

[Serializable, NetSerializable]
public enum NCCorpSalesUiKey : byte
{
    Key,
}

/// <summary>Progress of one requirement: how much of it is on the pallets now.</summary>
[Serializable, NetSerializable]
public sealed record NCCorpRequirementProgress(int Index, int Have, int Need);

[Serializable, NetSerializable]
public sealed record NCCorpContractEntry(int Slot, ProtoId<NCCorpContractPrototype> Contract, List<NCCorpRequirementProgress> Progress)
{
    public bool Ready => Progress.TrueForAll(progress => progress.Have >= progress.Need);
}

[Serializable, NetSerializable]
public sealed class NCCorpSalesUiState(
    ProtoId<NCCorpSalesPrototype> corporation,
    bool employee,
    bool palletFound,
    List<NCCorpContractEntry> contracts) : BoundUserInterfaceState
{
    public readonly ProtoId<NCCorpSalesPrototype> Corporation = corporation;
    public readonly bool Employee = employee;
    public readonly bool PalletFound = palletFound;
    public readonly List<NCCorpContractEntry> Contracts = contracts;
}

[Serializable, NetSerializable]
public sealed class NCCorpSalesFulfillMessage(int slot) : BoundUserInterfaceMessage
{
    public readonly int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class NCCorpSalesRefreshMessage : BoundUserInterfaceMessage;
