// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed partial class DiagnosticApplication
{
    private async Task<JsonNode?> CaptureClientAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var started = await ForwardAsync("client_capture", arguments, cancellationToken);
        var captureId = started?["data"]?["captureId"]?.GetValue<string>()
            ?? throw new DiagnosticException("invalid_response", "The client returned no capture identifier.");
        var query = new JsonObject { ["targetId"] = arguments["targetId"]?.DeepClone(), ["captureId"] = captureId, ["consume"] = true,
            ["serverInstanceId"] = started!["serverInstanceId"]!.DeepClone(), ["worldGeneration"] = started["worldGeneration"]!.DeepClone() };
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        try
        {
        do
        {
            var response = await ForwardAsync("capture_get", query, cancellationToken);
            var data = response?["data"]?.AsObject() ?? throw new DiagnosticException("invalid_response", "The client returned no capture status.");
            switch (data["state"]?.GetValue<string>())
            {
                case "succeeded":
                    var bytes = Convert.FromBase64String(data.RequiredString("base64", 1_400_000));
                    var artifact = await Artifacts.WriteAsync("capture", "image/png", bytes, cancellationToken);
                    data.Remove("base64");
                    data["artifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions);
                    return response;
                case "failed":
                    throw new DiagnosticException(data["error"]?.GetValue<string>() ?? "capture_failed", "The client could not produce the requested image.");
            }
            await Task.Delay(100, cancellationToken);
        } while (DateTimeOffset.UtcNow < until);
        throw new DiagnosticException("capture_timeout", "No rendered capture arrived before the deadline.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await ForwardAsync("capture_discard", query, cleanup.Token); }
            catch (Exception exception) when (exception is DiagnosticException or OperationCanceledException) { }
        }
    }
}
