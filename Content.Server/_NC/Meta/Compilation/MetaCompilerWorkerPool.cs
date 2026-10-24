// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;

namespace Content.Server._NC.Meta.Compilation;

/// <summary>FIFO admission with two isolated workers, one outstanding job per principal and no game-thread compilation.</summary>
public sealed class MetaCompilerWorkerPool : IAsyncDisposable
{
    /// <summary>Build-selected canonical bundle outside scanned game assemblies. No runtime fallback search.</summary>
    public static string ResolvePublishedWorkerPath(string baseDirectory)
    {
        if (!Path.IsPathFullyQualified(baseDirectory)) throw new ArgumentException("Published base directory must be absolute.", nameof(baseDirectory));
#if FULL_RELEASE
        return Path.GetFullPath(Path.Combine(baseDirectory, "MetaCompilerWorker", "Meta.CompilerWorker.dll"));
#else
        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "MetaCompilerWorker", "Meta.CompilerWorker.dll"));
#endif
    }
    private readonly string _workerPath;
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, byte> _principals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime Expires, MetaCompilationResult Result)> _negativeCache = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _consumers;
    public MetaCompilerWorkerPool(string absoluteWorkerPath)
    {
        if (!Path.IsPathFullyQualified(absoluteWorkerPath) || !File.Exists(absoluteWorkerPath)) throw new FileNotFoundException("Published META compiler worker is unavailable.", absoluteWorkerPath);
        _workerPath = Path.GetFullPath(absoluteWorkerPath);
        _consumers = [Task.Run(ConsumeAsync), Task.Run(ConsumeAsync)];
    }

    public async Task<MetaCompilationResult> CompileAsync(string principal, MetaCompilationRequest request, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(principal) || principal.Length > 128) throw new ArgumentException("Principal is required and bounded.", nameof(principal));
        ValidateRequest(request);
        if (!_principals.TryAdd(principal, 0)) throw new InvalidOperationException("META principal already has a pending compilation.");
        var queued = false;
        try
        {
            // Limits are server policy, never a way for caller or source manifest to raise resource budgets.
            request = request with { Limits = null, Sources = request.Sources.ToArray(), Dependencies = request.Dependencies?.Select(d => d with { Sources = d.Sources.ToArray() }).ToArray(),
                BinaryDependencies = request.BinaryDependencies?.Select(d => d with { Artifact = d.Artifact.ToArray() }).ToArray() };
            var key = principal + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, MetaCompilerProtocol.JsonOptions)));
            if (_negativeCache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Result;
            var completion = new TaskCompletionSource<MetaCompilationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_jobs.Writer.TryWrite(new(principal, request, completion, cancellation))) throw new InvalidOperationException("META compile queue is full.");
            queued = true;
            var result = await completion.Task.WaitAsync(cancellation);
            if (!result.Success)
            {
                if (_negativeCache.Count >= 64) _negativeCache.Clear();
                _negativeCache[key] = (DateTime.UtcNow.AddSeconds(15), result);
            }
            return result;
        }
        finally { if (!queued) _principals.TryRemove(principal, out _); }
    }

    private static void ValidateRequest(MetaCompilationRequest request)
    {
        if (request.Sources.Count > 64 || request.Dependencies?.Count > 64 || request.BinaryDependencies?.Count > 64 || request.RootModule.Length > 128 || request.PackageName.Length > 128 || request.PackageVersion.Length > 128)
            throw new ArgumentException("META compilation request exceeds policy limits.", nameof(request));
        var count = 0; var bytes = 0;
        foreach (var source in request.Sources.Concat((request.Dependencies ?? []).SelectMany(d => d.Sources)))
        {
            if (++count > 64 || source.FileId.Length > 192 || source.Text.Length > 256 * 1024) throw new ArgumentException("META source exceeds policy limits.", nameof(request));
            var length = System.Text.Encoding.UTF8.GetByteCount(source.Text);
            if (length > 256 * 1024 || (bytes += length) > 1024 * 1024) throw new ArgumentException("META source exceeds policy limits.", nameof(request));
        }
        foreach (var dependency in request.Dependencies ?? [])
            if (dependency.PackageIdentity.Length > 128 || dependency.Version.Length > 128 || dependency.ContentHash.Length != 64)
                throw new ArgumentException("META dependency exceeds policy limits.", nameof(request));
        var binaryBytes = 0;
        foreach (var dependency in request.BinaryDependencies ?? [])
            if (dependency.Artifact.Length > 1024 * 1024 || (binaryBytes += dependency.Artifact.Length) > 4 * 1024 * 1024 || dependency.PackageIdentity.Length > 128 || dependency.Version.Length > 128 || dependency.ContentHash.Length != 64)
                throw new ArgumentException("META binary dependency exceeds policy limits.", nameof(request));
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(_shutdown.Token))
            {
                if (job.Cancellation.IsCancellationRequested) { job.Completion.TrySetCanceled(job.Cancellation); _principals.TryRemove(job.Principal, out _); continue; }
                try
                {
                    var result = await RunAsync(job.Request, job.Cancellation);
                    _principals.TryRemove(job.Principal, out _);
                    job.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { job.Completion.TrySetCanceled(); }
                catch (Exception exception) { job.Completion.TrySetException(exception); }
                finally { _principals.TryRemove(job.Principal, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<MetaCompilationResult> RunAsync(MetaCompilationRequest request, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("META compiler production workers require an installed, tested OS memory policy; Windows Job Objects are currently supported.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(_workerPath)! };
        if (Path.GetExtension(_workerPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var runtime = RuntimeEnvironment.GetRuntimeDirectory();
            var root = new DirectoryInfo(runtime).Parent?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("Cannot locate the current .NET runtime.");
            start.FileName = Path.Combine(root, "dotnet.exe");
            if (!File.Exists(start.FileName)) throw new FileNotFoundException("Current .NET host is unavailable.", start.FileName);
            start.ArgumentList.Add(_workerPath);
        }
        else start.FileName = _workerPath;
        var windowsRoot = Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment.Clear();
        if (windowsRoot != null) start.Environment["SystemRoot"] = windowsRoot;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start META compiler worker.");
        using var resourcePolicy = WindowsWorkerJob.Attach(process, 256L * 1024 * 1024);
        using var kill = deadline.Token.Register(() => TryKill(process));
        try
        {
            await MetaCompilerProtocol.WriteAsync(process.StandardInput.BaseStream, new MetaWorkerRequest(MetaCompilerProtocol.Version, MetaApiCatalog.Default.Hash, request), MetaCompilerProtocol.MaximumRequestBytes, deadline.Token);
            process.StandardInput.Close();
            var response = await MetaCompilerProtocol.ReadAsync<MetaWorkerResponse>(process.StandardOutput.BaseStream, MetaCompilerProtocol.MaximumResponseBytes, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || response.ProtocolVersion != MetaCompilerProtocol.Version || response.Diagnostics.Length > 100) throw new InvalidDataException("Invalid META compiler worker response.");
            foreach (var diagnostic in response.Diagnostics)
                if (diagnostic.Code.Length > 16 || diagnostic.Arguments.Sum(a => a.Length) > 4096 || diagnostic.Related.Count > 8 || diagnostic.StartUtf8Byte < 0 || diagnostic.EndUtf8Byte < diagnostic.StartUtf8Byte)
                    throw new InvalidDataException("Invalid META diagnostic response.");
            MetaModule? module = null;
            MetaExportCertificate? exports = null;
            if (response.Artifact != null)
            {
                module = MetaBytecodeLoader.Load(response.Artifact);
                var verified = MetaVerifier.Verify(response.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(module));
                if (response.ArtifactHash != verified.Digest) throw new InvalidDataException("Worker artifact digest mismatch.");
                exports = new MetaCompiler().VerifyExports(request, verified, deadline.Token);
            }
            return new(response.Artifact, response.ArtifactHash, response.CacheIdentity, response.Diagnostics, module, response.Analysis, exports);
        }
        finally { TryKill(process); }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.Writer.TryComplete(); _shutdown.Cancel();
        await Task.WhenAll(_consumers);
        while (_jobs.Reader.TryRead(out var job)) job.Completion.TrySetCanceled();
        _shutdown.Dispose();
    }
    private sealed record Job(string Principal, MetaCompilationRequest Request, TaskCompletionSource<MetaCompilationResult> Completion, CancellationToken Cancellation);

    private sealed class WindowsWorkerJob : IDisposable
    {
        private readonly nint _handle;
        private WindowsWorkerJob(nint handle) => _handle = handle;
        public static WindowsWorkerJob Attach(Process process, long memoryBytes)
        {
            var handle = CreateJobObject(nint.Zero, null);
            if (handle == nint.Zero) { TryKill(process); throw new System.ComponentModel.Win32Exception(); }
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 | 0x00000100 | 0x00000008, ActiveProcessLimit = 1 }, ProcessMemoryLimit = (nuint) memoryBytes };
            if (!SetInformationJobObject(handle, 9, ref limits, (uint) Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(handle, process.Handle))
            {
                CloseHandle(handle); process.Kill(true); throw new System.ComponentModel.Win32Exception();
            }
            return new(handle);
        }
        public void Dispose() => CloseHandle(_handle);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize; public nuint MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass; public uint SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit; public nuint JobMemoryLimit; public nuint PeakProcessMemoryUsed; public nuint PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimits information, uint informationLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    }
}
