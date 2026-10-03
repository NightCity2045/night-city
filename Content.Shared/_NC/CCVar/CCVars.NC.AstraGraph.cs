// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Public HTTPS address of Astra Studio Web. The game client never starts a local HTTP listener.
    /// </summary>
    public static readonly CVarDef<string> NCAstraStudioUrl =
        CVarDef.Create("nc.astra.studio_url", string.Empty, CVar.SERVERONLY);
}
