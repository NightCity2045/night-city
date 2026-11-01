// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed record DiagnosticOperation(
    string Id, string Name, string State, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt,
    bool CancellationRequested, JsonNode? Result, DiagnosticFault? Error);

/// <summary>Bounded operation registry. Transport loss is never interpreted as a failed mutation.</summary>
public sealed class OperationManager : IDisposable
{
    private sealed class Entry(string id, string name, string fingerprint, bool mutation)
    {
        public readonly object Gate = new();
        public readonly string Id = id;
        public readonly string Name = name;
        public readonly string Fingerprint = fingerprint;
        public readonly bool Mutation = mutation;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public readonly CancellationTokenSource Cancellation = new();
        public string State = "accepted";
        public DateTimeOffset? FinishedAt;
        public JsonNode? Result;
        public DiagnosticFault? Error;
        public int ResultBytes;
    }

    private readonly object _admissionGate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;
    private long _retainedBytes;

    public DiagnosticOperation Start(string name, string id, string fingerprint, bool mutation,
        Func<CancellationToken, Task<JsonNode?>> execute)
    {
        if (id.Length is < 1 or > 128 || !id.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
            throw new DiagnosticException("invalid_arguments", "Invalid operation identifier.");
        Entry entry;
        lock (_admissionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(id, out var previous))
            {
                if (previous.Fingerprint != fingerprint || previous.Name != name)
                    throw new DiagnosticException("idempotency_conflict", "This operation identifier belongs to different arguments.");
                return Snapshot(previous);
            }
            foreach (var pair in _entries)
            {
                lock (pair.Value.Gate)
                {
                    if (pair.Value.FinishedAt is { } finished && finished < DateTimeOffset.UtcNow.AddMinutes(-10) &&
                        _entries.TryRemove(pair.Key, out var removed))
                    {
                        Interlocked.Add(ref _retainedBytes, -removed.ResultBytes);
                        removed.Cancellation.Dispose();
                    }
                }
            }
            if (_entries.Count >= 128 || Volatile.Read(ref _retainedBytes) >= 64 * 1024 * 1024 ||
                _entries.Values.Count(item => { lock (item.Gate) return item.FinishedAt == null; }) >= 8)
                throw new DiagnosticException("busy", "The operation registry is full. Completed operations expire after ten minutes.", true);
            entry = new Entry(id, name, fingerprint, mutation);
            _entries[id] = entry;
        }
        _ = Task.Run(() => RunAsync(entry, execute));
        return Snapshot(entry);
    }

    public DiagnosticOperation Get(string id) => Snapshot(Find(id));

    public DiagnosticOperation Cancel(string id)
    {
        var entry = Find(id);
        lock (entry.Gate)
        {
            if (entry.FinishedAt == null)
                entry.Cancellation.Cancel();
            return Snapshot(entry);
        }
    }

    private async Task RunAsync(Entry entry, Func<CancellationToken, Task<JsonNode?>> execute)
    {
        lock (entry.Gate)
        {
            if (entry.Cancellation.IsCancellationRequested)
            {
                entry.State = "cancelled";
                entry.FinishedAt = DateTimeOffset.UtcNow;
                return;
            }
            entry.State = "running";
        }
        try
        {
            var result = await execute(entry.Cancellation.Token);
            lock (entry.Gate)
            {
                entry.Result = result?.DeepClone();
                entry.State = "succeeded";
            }
        }
        catch (DiagnosticOperationFailedException failure)
        {
            lock (entry.Gate)
            {
                entry.State = failure.State;
                entry.Result = failure.Result?.DeepClone();
                entry.Error = failure.Fault;
            }
        }
        catch (OperationCanceledException)
        {
            lock (entry.Gate)
            {
                entry.State = entry.Mutation ? "indeterminate" : "cancelled";
                entry.Error = entry.Mutation
                    ? new DiagnosticFault("outcome_unknown", "Cancellation cannot confirm whether an in-flight mutation completed.")
                    : null;
            }
        }
        catch (DiagnosticException exception)
        {
            lock (entry.Gate)
            {
                entry.State = entry.Mutation && exception.Code is "timeout" or "bridge_unavailable" or "outcome_unknown" ? "indeterminate" : "failed";
                entry.Error = new DiagnosticFault(exception.Code, exception.Message, exception.Retryable);
            }
        }
        catch (Exception)
        {
            lock (entry.Gate)
            {
                entry.State = entry.Mutation ? "indeterminate" : "failed";
                entry.Error = new DiagnosticFault("internal_error", "The operation stopped unexpectedly. Consult the local diagnostic log.");
            }
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.ResultBytes = Encoding.UTF8.GetByteCount(entry.Result?.ToJsonString() ?? "null");
                Interlocked.Add(ref _retainedBytes, entry.ResultBytes);
                entry.FinishedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private Entry Find(string id) => _entries.TryGetValue(id, out var entry) ? entry :
        throw new DiagnosticException("operation_not_found", "The operation does not exist in this MCP process.");

    private static DiagnosticOperation Snapshot(Entry entry)
    {
        lock (entry.Gate)
            return new DiagnosticOperation(entry.Id, entry.Name, entry.State, entry.CreatedAt, entry.FinishedAt,
                entry.Cancellation.IsCancellationRequested, entry.Result?.DeepClone(), entry.Error);
    }

    public void Dispose()
    {
        lock (_admissionGate)
        {
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Cancellation.Cancel();
        }
    }
}
