// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.CCVar;

/// <summary>
/// Night City job-system configuration.
/// </summary>
[CVarDefs]
public sealed partial class NCCVars : CVars
{
    /// <summary>
    /// Replaces ordinary station job selection with the technical Citizen role.
    /// </summary>
    public static readonly CVarDef<bool> SingleCitizenJob =
        CVarDef.Create(
            "nc.jobs.single_citizen",
            true,
            CVar.ARCHIVE | CVar.SERVER | CVar.REPLICATED);
}
