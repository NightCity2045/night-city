// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public static class DiagnosticBuild
{
    public static void Require(string assembly, string expectedNamespace, string expectedType)
    {
        using var stream = File.OpenRead(assembly);
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.StringComparer.Equals(type.Name, expectedType) && reader.StringComparer.Equals(type.Namespace, expectedNamespace))
                return;
        }
        throw new DiagnosticException("diagnostic_build_required", "Build the requested game project with -c DebugOpt -p:EnableNCDiagnostics=true before starting a diagnostic environment.");
    }
}
