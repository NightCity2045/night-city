// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Bank.Budget;

[Serializable, NetSerializable]
public enum NCOrganizationBudgetConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record NCOrganizationBudgetTransactionSummary(
    long Id,
    string Type,
    int Amount,
    int BalanceAfter,
    string ActorName,
    string Reason,
    DateTime CreatedAt);

[Serializable, NetSerializable]
public sealed class NCOrganizationBudgetConsoleState(
    string organizationName,
    int balance,
    int insertedCash,
    bool canManage,
    List<NCOrganizationBudgetTransactionSummary> transactions) : BoundUserInterfaceState
{
    public readonly string OrganizationName = organizationName;
    public readonly int Balance = balance;
    public readonly int InsertedCash = insertedCash;
    public readonly bool CanManage = canManage;
    public readonly List<NCOrganizationBudgetTransactionSummary> Transactions = transactions;
}

[Serializable, NetSerializable]
public sealed class NCOrganizationBudgetDepositMessage(string reason) : BoundUserInterfaceMessage
{
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCOrganizationBudgetWithdrawMessage(int amount, string reason) : BoundUserInterfaceMessage
{
    public readonly int Amount = amount;
    public readonly string Reason = reason;
}
