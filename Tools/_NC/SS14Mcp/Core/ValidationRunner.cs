// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed class ValidationRunner(WorkspacePaths paths, ArtifactStore artifacts)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JsonNode?> RunAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var kind = arguments.RequiredString("kind", 64);
        if (kind == "integration" && Environment.GetEnvironmentVariable("SS14_MCP_ALLOW_INTEGRATION_TESTS") != "1")
            throw new DiagnosticException("integration_opt_in_required", "Set SS14_MCP_ALLOW_INTEGRATION_TESTS=1 before starting this MCP process to permit integration tests.");
        string[] command = kind switch
        {
            "build-server" => ["build", "Content.Server/Content.Server.csproj", "-c", "DebugOpt", "--no-restore", "--disable-build-servers", "/m:1", "-p:EnableNCDiagnostics=true"],
            "build-client" => ["build", "Content.Client/Content.Client.csproj", "-c", "DebugOpt", "--no-restore", "--disable-build-servers", "/m:1", "-p:EnableNCDiagnostics=true"],
            "content" => ["test", "Content.Tests/Content.Tests.csproj", "-c", "DebugOpt", "--no-build", "--no-restore", "/m:1", "--", "NUnit.ConsoleOut=0"],
            "integration" => ["test", "Content.IntegrationTests/Content.IntegrationTests.csproj", "-c", "DebugOpt", "--no-build", "--no-restore", "/m:1", "--", "NUnit.ConsoleOut=0"],
            "yaml" => ["run", "--project", "Content.YAMLLinter/Content.YAMLLinter.csproj", "-c", "DebugOpt", "--no-restore"],
            "mcp" => ["test", "Tools/_NC/SS14Mcp/Tests/SS14Mcp.Tests.csproj", "--no-restore", "/m:1"],
            "rsi" => [paths.Resolve("RobustToolbox/Schemas/validate_rsis.py"), paths.Resolve("Resources")],
            _ => throw new DiagnosticException("unsupported_validation", "Validation kind must be build-server, build-client, content, integration, yaml, mcp or rsi."),
        };
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new DiagnosticException("busy", "Another validation is running in this MCP process.", true);
        try
        {
            var logs = new DiagnosticLogBuffer(1024);
            var start = new ProcessStartInfo(kind == "rsi" ? Environment.GetEnvironmentVariable("SS14_MCP_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3") : "dotnet")
            {
                WorkingDirectory = paths.Root, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment["MSBuildEnableWorkloadResolver"] = "false";
            foreach (var argument in command)
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, ev) => { if (ev.Data != null) logs.Add("info", kind, ev.Data); };
            process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) logs.Add("error", kind, ev.Data); };
            var clock = Stopwatch.StartNew();
            try
            {
                if (!process.Start())
                    throw new DiagnosticException("validation_start_failed", "Could not start the selected validation tool.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cancellationToken);
                // Ensure async stdout/stderr callbacks have drained before reading the final page.
                process.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new DiagnosticException("validation_tool_missing", "The selected validation executable is unavailable.");
            }
            var result = new JsonObject { ["kind"] = kind, ["passed"] = process.ExitCode == 0, ["exitCode"] = process.ExitCode,
                ["milliseconds"] = clock.ElapsedMilliseconds,
                ["output"] = JsonSerializer.SerializeToNode(logs.Snapshot(), DiagnosticProtocol.JsonOptions),
                ["outputBoundedToLastLines"] = 1024 };
            var artifact = await artifacts.WriteJsonAsync("validation", result, CancellationToken.None);
            result["artifact"] = JsonSerializer.SerializeToNode(artifact, DiagnosticProtocol.JsonOptions);
            if (process.ExitCode != 0)
                throw new DiagnosticOperationFailedException(new DiagnosticFault("validation_failed", "The selected validation failed. Read the retained output artifact."), result);
            return result;
        }
        finally { _gate.Release(); }
    }
}
