// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared._NC.Bank;

/// <summary>
/// What a bank history line records.
/// </summary>
public enum NCBankTransactionKind : byte
{
    OpeningBalance,
    CashDeposit,
    CashWithdrawal,
    TransferOut,
    TransferIn,
    Salary,
    AdminGrant,
    AdminWithdrawal,
    SalaryMissed,
    ContractReward,
    FixerPayment,
}
