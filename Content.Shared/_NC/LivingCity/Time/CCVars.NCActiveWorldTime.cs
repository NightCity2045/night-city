// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Active round-time accumulated between durable world-clock checkpoints.
    /// A checkpoint is also forced when the round leaves <c>InRound</c>.
    /// </summary>
    public static readonly CVarDef<float> NCActiveWorldClockCheckpointSeconds = CVarDef.Create(
        "nc.living_city.active_world_clock.checkpoint_seconds",
        30f,
        CVar.SERVERONLY);
}
