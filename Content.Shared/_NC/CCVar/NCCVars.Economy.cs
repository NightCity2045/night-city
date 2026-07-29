// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Configuration;

namespace Content.Shared._NC.CCVar;

public sealed partial class NCCVars
{
    public static readonly CVarDef<string> BankCurrency =
        CVarDef.Create("nc.bank.currency", "Eurodollar", CVar.SERVERONLY);

    public static readonly CVarDef<long> BankStartingBalance =
        CVarDef.Create("nc.bank.starting_balance", 0L, CVar.SERVERONLY);

    public static readonly CVarDef<float> PayrollCheckInterval =
        CVarDef.Create("nc.bank.payroll_check_interval_seconds", 5f, CVar.SERVERONLY);
}
