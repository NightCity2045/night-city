// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NightCity.Diagnostics.Contracts;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "artifact_image", ReadOnly = true), Description("Return a retained PNG as native MCP image content. Only opaque IDs from this workspace's artifact store are accepted.")]
    public async Task<CallToolResult> ArtifactImage(string id, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await application.Artifacts.ReadImageAsync(id, cancellationToken);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(bytes, "image/png")] };
        }
        catch (DiagnosticException exception)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock
                { Text = DiagnosticProtocol.Serialize(new DiagnosticFault(exception.Code, exception.Message, exception.Retryable)) }] };
        }
    }
}
