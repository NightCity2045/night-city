// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;
using Content.Shared.Database._NC.Police;

namespace Content.Shared._NC.Bank.ATM;

[Serializable, NetSerializable]
public enum NCAtmUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class NCAtmBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly int BankBalance;
    public readonly string AccountNumber;
    public readonly bool IsLoggedIn;
    public readonly float TaxRate;
    public readonly int DepositAmount;
    public readonly string OwnAccountNumber;
    public readonly List<NCAtmFineSummary> Fines;

    public NCAtmBoundUserInterfaceState(
        int bankBalance,
        string accountNumber,
        bool isLoggedIn,
        float taxRate,
        int depositAmount,
        string ownAccountNumber,
        List<NCAtmFineSummary> fines)
    {
        BankBalance = bankBalance;
        AccountNumber = accountNumber;
        IsLoggedIn = isLoggedIn;
        TaxRate = taxRate;
        DepositAmount = depositAmount;
        OwnAccountNumber = ownAccountNumber;
        Fines = fines;
    }
}

[Serializable, NetSerializable]
public sealed class NCAtmLoginMessage(string accountNumber, string pin) : BoundUserInterfaceMessage
{
    public readonly string AccountNumber = accountNumber;
    public readonly string Pin = pin;
}

[Serializable, NetSerializable]
public sealed class NCAtmLogoutMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCAtmWithdrawMessage(int amount) : BoundUserInterfaceMessage
{
    public readonly int Amount = amount;
}

[Serializable, NetSerializable]
public sealed class NCAtmDepositMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed record NCAtmFineSummary(long Id, string Article, string Reason, int Amount,
    NCPoliceFineStatus Status, DateTime DueAt);

[Serializable, NetSerializable]
public sealed class NCAtmPayFineMessage(long fineId) : BoundUserInterfaceMessage
{
    public readonly long FineId = fineId;
}
