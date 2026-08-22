// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.Configuration;

/// <summary>
/// Server-only Night City configuration. Secrets never belong in CVars; the neural API key is owned by the
/// isolated cognition service.
/// </summary>
[CVarDefs]
public sealed class NCCVars : CVars
{
    public static readonly CVarDef<bool> NeuralEnabled =
        CVarDef.Create("nc_neural.enabled", false, CVar.SERVERONLY);

    /// <summary>An empty value keeps the endpoint authored by the neural policy prototype.</summary>
    public static readonly CVarDef<string> NeuralEndpoint =
        CVarDef.Create("nc_neural.endpoint", string.Empty, CVar.SERVERONLY);
}
