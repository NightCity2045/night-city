// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed record RuntimeEnvironmentInfo(string Id, string State, int Port, string[] Targets, int[] ProcessIds, string Directory);

/// <summary>Owns only processes it creates. Every runtime gets separate server data and staged client user data.</summary>
public sealed class RuntimeSupervisor(WorkspacePaths paths, IDiagnosticBridge bridge) : IDisposable
{
    private sealed class OwnedEnvironment(string id, string directory, int port)
    {
        public readonly string Id = id;
        public readonly string Directory = directory;
        public readonly int Port = port;
        public readonly List<Process> Processes = [];
        public readonly List<DiagnosticTarget> Targets = [];
        public readonly DiagnosticLogBuffer Logs = new(4096);
        public readonly object Gate = new();
        public readonly CancellationTokenSource Lifetime = new();
        public readonly Dictionary<string, string> BinaryHashes = new();
        public ResourceSnapshotInfo? Resources;
        public string? BuildId;
        public NightCity.Diagnostics.Seeds.SqliteSeedArtifact.Report? SqlitePrefill;
        public string State = "starting";
    }

    private readonly ConcurrentDictionary<string, OwnedEnvironment> _environments = new();
    private readonly SemaphoreSlim _startupGate = new(1, 1);

    public RuntimeEnvironmentInfo[] List() => _environments.Values.Select(Describe).ToArray();

    public DiagnosticLogPage Logs(string id, long cursor, int limit) => Find(id).Logs.Read(cursor, limit);

    public async Task<(RuntimeEnvironmentInfo Info, DiagnosticTarget[] Targets)> StartAsync(int clients, bool graphics,
        CancellationToken cancellationToken)
    {
        if (clients is < 0 or > 3)
            throw new DiagnosticException("invalid_arguments", "An environment may contain zero to three clients.");
        await _startupGate.WaitAsync(cancellationToken);
        OwnedEnvironment? environment = null;
        try
        {
            if (_environments.Count >= 24)
                throw new DiagnosticException("environment_limit", "Restart the owning MCP process after 24 retained environments to release its bounded log registry.");
            if (_environments.Values.Count(x => x.State != "stopped") >= 3)
                throw new DiagnosticException("environment_limit", "At most three owned environments may run at once.");
            var build = DiagnosticRuntimeBuild.Resolve(paths, Environment.GetEnvironmentVariable("SS14_MCP_BUILD_ID"));
            var sqliteSeed = SqliteSeedPrefill.ReadEnvironment();
            var serverAssembly = build.ServerAssembly;
            var clientAssembly = build.ClientAssembly;
            if (!File.Exists(serverAssembly) || clients > 0 && !File.Exists(clientAssembly))
                throw new DiagnosticException("build_required", "Build the server and requested client before starting the environment.");
            DiagnosticBuild.Require(serverAssembly, "Content.Server._NC.Diagnostics", "NCDiagnosticsSystem");
            if (clients > 0)
                DiagnosticBuild.Require(clientAssembly, "Content.Client._NC.Diagnostics", "NCDiagnosticsClientSystem");
            var id = Guid.NewGuid().ToString("N");
            var directory = paths.Resolve("artifacts/ss14-mcp/environments/" + id);
            System.IO.Directory.CreateDirectory(directory);
            var port = ChoosePort();
            environment = new OwnedEnvironment(id, directory, port);
            environment.BuildId = build.BuildId;
            if (!_environments.TryAdd(id, environment))
                throw new DiagnosticException("environment_conflict", "The environment identifier is already in use.");
            environment.BinaryHashes["server"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(serverAssembly, cancellationToken)));
            if (clients > 0)
                environment.BinaryHashes["client"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(clientAssembly, cancellationToken)));
            await WriteManifestAsync(environment, cancellationToken);
            environment.Resources = await IsolatedResourceSnapshot.CaptureAsync(paths, build.Resources,
                Path.Combine(directory, "resource-snapshot"), cancellationToken);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, environment.Lifetime.Token);
            cancellationToken = startup.Token;
            var serverToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var serverTarget = new DiagnosticTarget(id + "-server", "server", $"http://127.0.0.1:{port}/", serverToken, id);
            lock (environment.Gate)
                environment.Targets.Add(serverTarget);
            var configPath = Path.Combine(directory, "server.toml");
            await File.WriteAllTextAsync(configPath, $"""
                [net]
                port = {port}
                bindto = "127.0.0.1"
                upnp = false
                max_connections = 8
                [status]
                enabled = true
                bind = "127.0.0.1:{port}"
                [auth]
                mode = 2
                [hub]
                advertise = false
                [game]
                hostname = "SS14 MCP isolated playtest"
                dummyticker = false
                defaultpreset = "Sandbox"
                map = "Empty"
                map_rotation = false
                lobbyenabled = false
                auto_pause_empty = false
                [database]
                engine = "sqlite"
                sqlite_dbpath = "preferences.db"
                [nc_neural]
                enabled = false
                [config]
                preset_development = false
                preset_debug = false
                """, cancellationToken);
            var serverDirectory = Path.Combine(directory, "server");
            var serverAssemblyDirectory = Path.Combine(serverDirectory, "bin", "Content.Server");
            StageBinaries(Path.GetDirectoryName(serverAssembly)!, serverAssemblyDirectory, cancellationToken);
            PrepareResourceRoots(serverDirectory);
            if (sqliteSeed != null)
            {
                environment.SqlitePrefill = await sqliteSeed.MaterializeAsync(paths, id, cancellationToken);
                environment.Logs.Add("info", "supervisor", "Validated pristine SQLite prefill; normal server migrations will still run.");
                await WriteManifestAsync(environment, cancellationToken);
            }
            var serverArgs = new[] { Path.Combine(serverAssemblyDirectory, "Content.Server.dll"), "--config-file", configPath,
                "--data-dir", Path.Combine(directory, "server-data"), "--mount-dir", paths.Resolve("RobustToolbox/Resources"),
                "--mount-dir", environment.Resources.Directory };
            StartProcess(environment, "server", serverArgs, new Dictionary<string, string>
            {
                [DiagnosticProtocol.TokenEnvironment] = serverToken,
                [DiagnosticProtocol.ProfileEnvironment] = "fixture-admin",
                ["NC_DIAGNOSTICS_ISOLATED"] = "1",
            });
            await WaitForBridgeAsync(environment, serverTarget, cancellationToken);
            for (var index = 0; index < clients; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clientId = id + "-client" + (index + 1);
                var clientDirectory = Path.Combine(directory, "client" + (index + 1));
                var assemblyDirectory = Path.Combine(clientDirectory, "bin", "Content.Client");
                StageBinaries(Path.GetDirectoryName(clientAssembly)!, assemblyDirectory, cancellationToken);
                // Development Robust resolves default resource mounts relative to bin/Content.Client.
                // Preserve that layout; empty defaults are supplemented with the real read-only resource mounts.
                PrepareResourceRoots(clientDirectory);
                var pipe = "ss14-mcp-" + Guid.NewGuid().ToString("N");
                var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                var arguments = new List<string> { Path.Combine(assemblyDirectory, "Content.Client.dll"), "--self-contained",
                    "--connect", "--connect-address", $"127.0.0.1:{port}", "--username", "McpTest" + (index + 1),
                    "--mount-dir", paths.Resolve("RobustToolbox/Resources"),
                    "--mount-dir", environment.Resources.Directory };
                if (!graphics)
                {
                    arguments.Add("--headless");
                    arguments.Add("--cvar");
                    arguments.Add("res.texturepreloadingenabled=false");
                }
                StartProcess(environment, "client" + (index + 1), arguments, new Dictionary<string, string>
                {
                    [DiagnosticProtocol.TokenEnvironment] = token,
                    [DiagnosticProtocol.ProfileEnvironment] = "playtest",
                    [DiagnosticProtocol.PipeEnvironment] = pipe,
                    ["NC_DIAGNOSTICS_ISOLATED"] = "1",
                });
                var target = new DiagnosticTarget(clientId, "client", pipe, token, id);
                lock (environment.Gate)
                    environment.Targets.Add(target);
                await WaitForBridgeAsync(environment, target, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (environment.Gate)
                environment.State = "running";
            await WriteManifestAsync(environment, cancellationToken);
            return (Describe(environment), environment.Targets.ToArray());
        }
        catch (Exception failure)
        {
            if (environment != null)
            {
                await StopAsync(environment.Id, CancellationToken.None);
                if (failure is DiagnosticException diagnostic)
                    throw new DiagnosticOperationFailedException(new DiagnosticFault(diagnostic.Code, diagnostic.Message, diagnostic.Retryable),
                        new JsonObject { ["environment"] = JsonSerializer.SerializeToNode(Describe(environment), DiagnosticProtocol.JsonOptions),
                            ["logs"] = JsonSerializer.SerializeToNode(environment.Logs.Snapshot().TakeLast(100), DiagnosticProtocol.JsonOptions) });
            }
            throw;
        }
        finally
        {
            _startupGate.Release();
        }
    }

    public async Task<RuntimeEnvironmentInfo> StopAsync(string id, CancellationToken cancellationToken)
    {
        var environment = Find(id);
        Process[] processes;
        lock (environment.Gate)
        {
            if (environment.State == "stopped")
                return Describe(environment);
            environment.Lifetime.Cancel();
            environment.State = "stopping";
            processes = environment.Processes.ToArray();
        }
        foreach (var process in processes.Reverse())
        {
            try
            {
                if (process.HasExited)
                    continue;
                if (!process.CloseMainWindow())
                    await process.StandardInput.WriteLineAsync("shutdown".AsMemory(), cancellationToken);
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                environment.Logs.Add("warning", "supervisor", "Could not gracefully stop an owned process.");
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        lock (environment.Gate)
            environment.State = "stopped";
        await File.WriteAllTextAsync(Path.Combine(environment.Directory, "process-output.json"),
            DiagnosticProtocol.Serialize(environment.Logs.Snapshot()), CancellationToken.None);
        await WriteManifestAsync(environment, CancellationToken.None);
        return Describe(environment);
    }

    private void StartProcess(OwnedEnvironment environment, string category, IEnumerable<string> arguments,
        Dictionary<string, string> variables)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = paths.Root, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        SqliteSeedPrefill.ClearBootstrapEnvironment(start);
        foreach (var (key, value) in variables)
            start.Environment[key] = value;
        using (var owner = Process.GetCurrentProcess())
        {
            start.Environment["NC_DIAGNOSTICS_OWNER_PID"] = owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["NC_DIAGNOSTICS_OWNER_START_TICKS"] = owner.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, ev) => { if (ev.Data != null) environment.Logs.Add("info", category, ev.Data); };
        process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) environment.Logs.Add("error", category, ev.Data); };
        lock (environment.Gate)
        {
            environment.Lifetime.Token.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new DiagnosticException("process_start_failed", "Could not start an owned game process.");
            environment.Processes.Add(process);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private Task WaitForBridgeAsync(OwnedEnvironment environment, DiagnosticTarget target, CancellationToken cancellationToken) =>
        RuntimeStartupWaiter.WaitAsync(bridge, target, () =>
        {
            lock (environment.Gate)
                return environment.Processes.Any(process => process.HasExited);
        }, cancellationToken);

    private static void PrepareResourceRoots(string directory)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "Resources"));
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "RobustToolbox", "Resources"));
    }

    private void StageBinaries(string source, string destination, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(destination);
        // --self-contained locates user_data beside Robust.Client.dll. Each client therefore needs its own assembly directory.
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Resolve(Path.GetRelativePath(paths.Root, file));
            if (Path.GetExtension(file) is ".dll" or ".json" or ".exe" or ".so" or ".dylib" or ".txt")
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        // META resolves only this canonical worker bundle. Top-level DLL copies
        // cannot substitute for its deps/runtimeconfig and matching core.
        var workerSource = paths.Resolve(Path.GetFullPath(Path.Combine(source, "..", "MetaCompilerWorker")));
        if (File.Exists(Path.Combine(source, "Content.Server.dll")))
        {
            string[] workerFiles = ["Meta.CompilerWorker.dll", "Meta.CompilerWorker.deps.json",
                "Meta.CompilerWorker.runtimeconfig.json", "Content.Meta.dll"];
            foreach (var name in workerFiles)
            {
                var file = paths.Resolve(Path.Combine(workerSource, name));
                if (!File.Exists(file))
                    throw new DiagnosticException("build_required", "The canonical META compiler worker bundle is incomplete.");
            }
            var core = paths.Resolve(Path.Combine(source, "Content.Meta.dll"));
            if (!File.Exists(core) || !SHA256.HashData(File.ReadAllBytes(core)).AsSpan().SequenceEqual(
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(workerSource, "Content.Meta.dll")))))
                throw new DiagnosticException("build_required", "Runtime and compiler worker contain different META cores.");
            var workerDestination = paths.Resolve(Path.GetFullPath(Path.Combine(destination, "..", "MetaCompilerWorker")));
            System.IO.Directory.CreateDirectory(workerDestination);
            foreach (var name in workerFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(Path.Combine(workerSource, name), Path.Combine(workerDestination, name), overwrite: false);
            }
        }
        var runtimes = Path.Combine(source, "runtimes");
        if (!System.IO.Directory.Exists(runtimes))
            return;
        foreach (var file in System.IO.Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Resolve(Path.GetRelativePath(paths.Root, file));
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static int ChoosePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private OwnedEnvironment Find(string id) => _environments.TryGetValue(id, out var environment) ? environment :
        throw new DiagnosticException("environment_not_found", "This process does not own that environment.");

    private static RuntimeEnvironmentInfo Describe(OwnedEnvironment environment)
    {
        lock (environment.Gate)
            return new RuntimeEnvironmentInfo(environment.Id, environment.State, environment.Port,
                environment.Targets.Select(x => x.Id).ToArray(), environment.Processes.Select(x => x.Id).ToArray(), environment.Directory);
    }

    private static Task WriteManifestAsync(OwnedEnvironment environment, CancellationToken cancellationToken)
    {
        object manifest;
        lock (environment.Gate)
            manifest = new { Environment = Describe(environment), BinarySha256 = environment.BinaryHashes, ResourceSnapshot = environment.Resources, AcceptanceBuildId = environment.BuildId,
                SqlitePrefill = environment.SqlitePrefill,
                OwnerProcessId = Environment.ProcessId,
                Processes = environment.Processes.Select(process => new { process.Id, StartedAtUtc = process.StartTime.ToUniversalTime() }).ToArray() };
        return File.WriteAllTextAsync(Path.Combine(environment.Directory, "manifest.json"),
            DiagnosticProtocol.Serialize(manifest), cancellationToken);
    }

    public void Dispose()
    {
        foreach (var environment in _environments.Values)
        {
            environment.Lifetime.Cancel();
            Process[] processes;
            lock (environment.Gate) processes = environment.Processes.ToArray();
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            lock (environment.Gate) environment.State = "stopped";
            try
            {
                WriteManifestAsync(environment, CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(environment.Directory, "process-output.json"), DiagnosticProtocol.Serialize(environment.Logs.Snapshot()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            foreach (var process in processes) process.Dispose();
        }
        _startupGate.Dispose();
    }
}
