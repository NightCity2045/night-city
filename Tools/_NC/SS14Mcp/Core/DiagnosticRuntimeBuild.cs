// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

/// <summary>Selects the common diagnostic build. Separate acceptance bundles were removed together with META.</summary>
public sealed record DiagnosticRuntimeBuild(string? BuildId, string ServerAssembly, string ClientAssembly, string Resources)
{
    public static DiagnosticRuntimeBuild Resolve(WorkspacePaths paths, string? buildId)
    {
        if (!string.IsNullOrEmpty(buildId))
            throw new DiagnosticException("invalid_build", "SS14_MCP_BUILD_ID is no longer supported; use the common diagnostic build.");
        return new(null,
            paths.Resolve("bin/NCDiagnostics/Content.Server/Content.Server.dll"),
            paths.Resolve("bin/NCDiagnostics/Content.Client/Content.Client.dll"), paths.Resolve("Resources"));
    }
}
