// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;
using Content.Shared.Database._NC.Police;
using Content.Shared._NC.LivingCity.Police;

namespace Content.Shared._NC.Bank.ATM;

[Serializable, NetSerializable]
public enum NCAtmUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class NCAtmBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly long BankBalance;
    public readonly string AccountNumber;
    public readonly bool IsLoggedIn;
    public readonly float TaxRate;
    public readonly int DepositAmount;
    public readonly string OwnAccountNumber;
    public readonly List<NCAtmFineSummary> Fines;

    public NCAtmBoundUserInterfaceState(
        long bankBalance,
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
public sealed record NCAtmFineSummary(NCPoliceFineId Id, string Article, string Reason, long Amount,
    NCPoliceFineStatus Status, TimeSpan DueIn);

[Serializable, NetSerializable]
public sealed class NCAtmPayFineMessage(NCPoliceFineId fineId) : BoundUserInterfaceMessage
{
    public readonly NCPoliceFineId FineId = fineId;
}
