// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Configuration;

namespace Content.Shared._NC.CCVar;

public sealed partial class NCCVars
{
    /// <summary>
    /// Disabled by default until the persistent economy has been validated in production.
    /// </summary>
    public static readonly CVarDef<bool> PermadeathEnabled =
        CVarDef.Create("nc.permadeath.enabled", false, CVar.SERVERONLY);
}
