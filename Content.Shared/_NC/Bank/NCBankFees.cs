// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared._NC.Bank;

public static class NCBankFees
{
    public const int BasisPointsPerUnit = 10000;

    /// <summary>The fee on an amount, rounded up so the bank never loses a cent.</summary>
    public static long Calculate(long amount, int basisPoints)
    {
        if (amount <= 0 || basisPoints <= 0)
            return 0;

        return (amount * basisPoints + BasisPointsPerUnit - 1) / BasisPointsPerUnit;
    }
}
