// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.Configuration;

/// <summary>
/// Server-only Night City configuration.
/// </summary>
[CVarDefs]
public sealed class NCCVars : CVars
{
    public static readonly CVarDef<bool> NeuralEnabled =
        CVarDef.Create("nc_neural.enabled", false, CVar.SERVERONLY);

    /// <summary>An empty value keeps the endpoint authored by the neural policy prototype.</summary>
    public static readonly CVarDef<string> NeuralEndpoint =
        CVarDef.Create("nc_neural.endpoint", string.Empty, CVar.SERVERONLY);

    /// <summary>Internal bearer shared only with NeuralCognitionService; this is not the OpenAI provider key.</summary>
    public static readonly CVarDef<string> NeuralServiceToken =
        CVarDef.Create("nc_neural.service_token", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>Legacy ignored alias retained only so an old server config does not fail CVar parsing.</summary>
    public static readonly CVarDef<string> NeuralApiKey =
        CVarDef.Create("nc_neural.api_key", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);
}
