// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Rolling period for explicit client diagnostic snapshot requests. Physical scanner interactions are already
    /// constrained by the interaction system and do not consume this network-only budget.
    /// </summary>
    public static readonly CVarDef<float> NCMedicalDiagnosticsRateLimitPeriod = CVarDef.Create(
        "nc.medical.diagnostics.rate_limit_period",
        1f,
        CVar.SERVERONLY);

    /// <summary>
    /// Maximum explicit requests accepted from one session during the configured period.
    /// </summary>
    public static readonly CVarDef<int> NCMedicalDiagnosticsRateLimitCount = CVarDef.Create(
        "nc.medical.diagnostics.rate_limit_count",
        4,
        CVar.SERVERONLY);
}
