// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.CartridgeLoader;
using Content.Shared.Eui;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Bank;

[Serializable, NetSerializable]
public enum NCBankUiKey : byte
{
    Atm,
    Terminal,
}

/// <summary>One operation of the current round shown in a bank UI.</summary>
[Serializable, NetSerializable]
public sealed record NCBankHistoryLine(
    NCBankTransactionKind Kind,
    long Amount,
    long Fee,
    string? Counterparty,
    string Note,
    TimeSpan RoundTime);

[Serializable, NetSerializable]
public sealed record NCBankSalaryLine(ProtoId<JobPrototype> Job, int Amount);

[Serializable, NetSerializable]
public sealed class NCAtmUiState(bool available, string accountNumber, long balance, int feeBasisPoints, int cashInSlot)
    : BoundUserInterfaceState
{
    public readonly bool Available = available;
    public readonly string AccountNumber = accountNumber;
    public readonly long Balance = balance;
    public readonly int FeeBasisPoints = feeBasisPoints;
    public readonly int CashInSlot = cashInSlot;
}

[Serializable, NetSerializable]
public sealed class NCBankDepositMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCBankWithdrawMessage(long amount) : BoundUserInterfaceMessage
{
    public readonly long Amount = amount;
}

[Serializable, NetSerializable]
public sealed class NCBankTerminalUiState(
    bool available,
    bool authorized,
    ProtoId<DepartmentPrototype>? department,
    string accountNumber,
    long balance,
    int feeBasisPoints,
    int cashInSlot,
    List<NCBankSalaryLine> salaries,
    List<NCBankHistoryLine> history) : BoundUserInterfaceState
{
    public readonly bool Available = available;
    public readonly bool Authorized = authorized;
    public readonly ProtoId<DepartmentPrototype>? Department = department;
    public readonly string AccountNumber = accountNumber;
    public readonly long Balance = balance;
    public readonly int FeeBasisPoints = feeBasisPoints;
    public readonly int CashInSlot = cashInSlot;
    public readonly List<NCBankSalaryLine> Salaries = salaries;
    public readonly List<NCBankHistoryLine> History = history;
}

[Serializable, NetSerializable]
public sealed class NCBankSetSalaryMessage(ProtoId<JobPrototype> job, int amount) : BoundUserInterfaceMessage
{
    public readonly ProtoId<JobPrototype> Job = job;
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class NCBankCartridgeUiState(
    bool available,
    string accountNumber,
    long balance,
    int feeBasisPoints,
    List<NCBankHistoryLine> history) : BoundUserInterfaceState
{
    public readonly bool Available = available;
    public readonly string AccountNumber = accountNumber;
    public readonly long Balance = balance;
    public readonly int FeeBasisPoints = feeBasisPoints;
    public readonly List<NCBankHistoryLine> History = history;
}

[Serializable, NetSerializable]
public sealed class NCBankCartridgeTransferMessage(string target, long amount) : CartridgeMessageEvent
{
    public readonly string Target = target;
    public readonly long Amount = amount;
}

[Serializable, NetSerializable]
public sealed record NCBankAdminAccountLine(string Number, ProtoId<DepartmentPrototype>? Department, string? Owner, long Balance);

[Serializable, NetSerializable]
public sealed record NCBankAdminFeeLine(string Category, ProtoId<DepartmentPrototype>? Department, int BasisPoints);

[Serializable, NetSerializable]
public sealed class NCBankAdminEuiState(
    List<NCBankAdminAccountLine> departments,
    List<NCBankAdminFeeLine> fees,
    NCBankAdminAccountLine? lookup,
    string? status) : EuiStateBase
{
    public readonly List<NCBankAdminAccountLine> Departments = departments;
    public readonly List<NCBankAdminFeeLine> Fees = fees;
    public readonly NCBankAdminAccountLine? Lookup = lookup;
    public readonly string? Status = status;
}

public static class NCBankAdminEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class Refresh : EuiMessageBase;

    [Serializable, NetSerializable]
    public sealed class Lookup(string number) : EuiMessageBase
    {
        public readonly string Number = number;
    }

    [Serializable, NetSerializable]
    public sealed class Adjust(string number, long amount, string reason) : EuiMessageBase
    {
        public readonly string Number = number;
        public readonly long Amount = amount;
        public readonly string Reason = reason;
    }

    [Serializable, NetSerializable]
    public sealed class SetFee(string category, int basisPoints) : EuiMessageBase
    {
        public readonly string Category = category;
        public readonly int BasisPoints = basisPoints;
    }
}
