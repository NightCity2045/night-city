// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared.Database._NC.Bank;

/// <summary>
/// What a bank journal line records. Stored as a byte, so only append new values.
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
}
