// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Shared._NC.Cargo.Pawn;

public static class NCPawnShopPricing
{
    public const long MaximumPayout = 1_000_000_000;

    public static bool TryGetPayout(double appraisal, int percentage, out long payout)
    {
        payout = 0;
        if (!double.IsFinite(appraisal) || appraisal <= 0 || percentage is < 1 or > 100)
            return false;
        var amount = Math.Floor(appraisal * percentage / 100);
        if (amount is < 1 or > MaximumPayout)
            return false;
        payout = (long) amount;
        return true;
    }
}
