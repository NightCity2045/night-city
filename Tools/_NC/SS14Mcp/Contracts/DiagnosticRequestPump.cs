// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightCity.Diagnostics.Contracts;

/// <summary>Transfers bounded transport requests to the owning game thread. Dispatch returns detached DTOs.</summary>
public sealed class DiagnosticRequestPump(string source)
{
    private sealed record Pending(DiagnosticRequest Request, TaskCompletionSource<DiagnosticResponse> Completion, CancellationToken Cancellation);
    private sealed record Cached(string Fingerprint, DiagnosticResponse Response, DateTimeOffset ExpiresAt, int Bytes);
    private readonly object _admission = new();
    private readonly ConcurrentQueue<Pending> _queue = new();
    private readonly Dictionary<string, Cached> _mutations = new(StringComparer.Ordinal);
    private int _queued;
    private int _cacheBytes;
    private long _generation = 1;
    private bool _stopped;
    private string? _leaseOwner;
    private string? _leaseToken;
    private DateTimeOffset _leaseExpiresAt;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public long WorldGeneration => Interlocked.Read(ref _generation);
    public int QueueLength => Volatile.Read(ref _queued);

    public async Task<DiagnosticResponse> EnqueueAsync(DiagnosticRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<DiagnosticResponse> completion;
        lock (_admission)
        {
            if (_stopped)
                return Error(request, "bridge_unavailable", "The diagnostic bridge has stopped.");
            if (QueueLength >= DiagnosticProtocol.MaximumQueueLength)
                return Error(request, "busy", "The game-thread diagnostic queue is full.", true);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _queued);
            _queue.Enqueue(new Pending(request, completion, cancellationToken));
        }
        return await completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Cooperative time budget; every dispatcher must also bound its individual operations.</summary>
    public void Process(long tick, Func<DiagnosticRequest, JsonNode?> dispatch, Func<string, bool> isMutation)
    {
        var start = Stopwatch.GetTimestamp();
        var processed = 0;
        while (processed < 2 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 2 && _queue.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref _queued);
            processed++;
            var request = pending.Request;
            if (pending.Cancellation.IsCancellationRequested)
            {
                pending.Completion.TrySetResult(Error(request, "cancelled", "The request was cancelled before owning-thread execution."));
                continue;
            }
            DiagnosticResponse response;
            try
            {
                var mutation = isMutation(request.Command);
                Validate(request, mutation);
                if (request.Command is "lease_acquire" or "lease_release")
                    response = Success(request, tick, ProcessLease(request));
                else if (request.Command == "request_status")
                {
                    Prune();
                    var id = request.Arguments.RequiredString("requestId", 128);
                    response = Success(request, tick, _mutations.TryGetValue(id, out var cached)
                        ? JsonSerializer.SerializeToNode(cached.Response, DiagnosticProtocol.JsonOptions)
                        : throw new DiagnosticException("request_not_found", "No retained mutation result exists for this request."));
                }
                else if (mutation)
                {
                    var fingerprint = Fingerprint(request);
                    Prune();
                    if (_mutations.TryGetValue(request.RequestId, out var cached))
                    {
                        if (cached.Fingerprint != fingerprint)
                            throw new DiagnosticException("idempotency_conflict", "The request identifier has different arguments.");
                        response = cached.Response;
                    }
                    else
                    {
                        if (_mutations.Count >= 512 || _cacheBytes > 8 * 1024 * 1024 - DiagnosticProtocol.MaximumResponseBytes)
                            throw new DiagnosticException("busy", "The mutation result registry is full.", true);
                        RequireLease(request);
                        response = Execute(request, tick, dispatch, true);
                        var size = Encoding.UTF8.GetByteCount(DiagnosticProtocol.Serialize(response));
                        _mutations[request.RequestId] = new Cached(fingerprint, response, DateTimeOffset.UtcNow.AddMinutes(10), size);
                        _cacheBytes += size;
                    }
                }
                else
                    response = Execute(request, tick, dispatch, false);
            }
            catch (DiagnosticException exception)
            {
                response = Error(request, exception.Code, exception.Message, exception.Retryable) with { CapturedTick = tick };
            }
            catch (Exception)
            {
                response = Error(request, "invalid_arguments", "The diagnostic request could not be processed.") with { CapturedTick = tick };
            }
            pending.Completion.TrySetResult(response);
        }
    }

    public void ResetWorld()
    {
        Interlocked.Increment(ref _generation);
        _mutations.Clear();
        _cacheBytes = 0;
        _leaseOwner = null;
        _leaseToken = null;
    }

    public void Stop()
    {
        lock (_admission)
        {
            _stopped = true;
            while (_queue.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queued);
                pending.Completion.TrySetResult(Error(pending.Request, "bridge_unavailable", "The diagnostic bridge has stopped."));
            }
        }
    }

    public DiagnosticResponse Error(DiagnosticRequest request, string code, string message, bool retryable = false) => new()
    {
        RequestId = request.RequestId, ServerInstanceId = InstanceId, WorldGeneration = WorldGeneration,
        Source = source, Error = new DiagnosticFault(code, message, retryable),
    };

    private DiagnosticResponse Success(DiagnosticRequest request, long tick, JsonNode? data) => new()
    {
        RequestId = request.RequestId, ServerInstanceId = InstanceId, WorldGeneration = WorldGeneration,
        CapturedTick = tick, Source = source, Data = data,
    };

    private DiagnosticResponse Execute(DiagnosticRequest request, long tick, Func<DiagnosticRequest, JsonNode?> dispatch, bool mutation)
    {
        try
        {
            var response = Success(request, tick, dispatch(request));
            if (Encoding.UTF8.GetByteCount(DiagnosticProtocol.Serialize(response)) > DiagnosticProtocol.MaximumResponseBytes)
                throw new DiagnosticException(mutation ? "outcome_unknown" : "response_too_large",
                    mutation ? "The mutation ran but its result exceeded the response limit. Inspect world state before retrying." : "Request a smaller diagnostic projection.");
            return response;
        }
        catch (DiagnosticException exception)
        {
            return Error(request, exception.Code, exception.Message, exception.Retryable) with { CapturedTick = tick };
        }
        catch (Exception)
        {
            return Error(request, mutation ? "outcome_unknown" : "internal_error", "The diagnostic request failed.") with { CapturedTick = tick };
        }
    }

    private void Validate(DiagnosticRequest request, bool mutation)
    {
        if (request.ApiVersion != DiagnosticProtocol.Version)
            throw new DiagnosticException("protocol_mismatch", "Unsupported diagnostic API version.");
        if (request.RequestId.Length is < 1 or > 128 || request.Command.Length is < 1 or > 64)
            throw new DiagnosticException("invalid_arguments", "Invalid request identifier or command.");
        if (request.DeadlineUnixMilliseconds < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new DiagnosticException("timeout", "The request expired before game-thread execution.");
        if (mutation && (request.ServerInstanceId == null || request.WorldGeneration == null))
            throw new DiagnosticException("world_reference_required", "Mutations must pin the process instance and world generation.");
        if (request.ServerInstanceId != null && request.ServerInstanceId != InstanceId ||
            request.WorldGeneration != null && request.WorldGeneration != WorldGeneration)
            throw new DiagnosticException("stale_world", "The request belongs to another process or world generation.");
    }

    private void Prune()
    {
        foreach (var key in _mutations.Where(x => x.Value.ExpiresAt < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
        {
            _cacheBytes -= _mutations[key].Bytes;
            _mutations.Remove(key);
        }
    }

    private JsonObject ProcessLease(DiagnosticRequest request)
    {
        var owner = request.Arguments.RequiredString("owner", 128);
        if (request.Command == "lease_release")
        {
            if (_leaseOwner == owner && DiagnosticWire.Authenticate(_leaseToken ?? string.Empty,
                request.Arguments["token"]?.GetValue<string>()))
            {
                _leaseOwner = null;
                _leaseToken = null;
            }
            return new JsonObject { ["released"] = _leaseOwner == null };
        }
        if (_leaseOwner != null && _leaseExpiresAt > DateTimeOffset.UtcNow &&
            (_leaseOwner != owner || !DiagnosticWire.Authenticate(_leaseToken ?? string.Empty,
                request.Arguments["token"]?.GetValue<string>())))
            throw new DiagnosticException("busy", "Another diagnostic session holds this target's mutation lease.", true);
        if (_leaseOwner != owner || _leaseExpiresAt <= DateTimeOffset.UtcNow)
            _leaseToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _leaseOwner = owner;
        _leaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(request.Arguments.BoundedInt("seconds", 30, 2, 30));
        return new JsonObject { ["token"] = _leaseToken, ["expiresAt"] = _leaseExpiresAt.ToUnixTimeMilliseconds() };
    }

    private void RequireLease(DiagnosticRequest request)
    {
        if (_leaseOwner != request.LeaseOwner || _leaseExpiresAt <= DateTimeOffset.UtcNow ||
            !DiagnosticWire.Authenticate(_leaseToken ?? string.Empty, request.LeaseToken))
            throw new DiagnosticException("lease_required", "Acquire this target's mutation lease before changing state.");
    }

    private static string Fingerprint(DiagnosticRequest request) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(request.Command + "\n" + request.ServerInstanceId + "\n" + request.WorldGeneration + "\n" + request.Arguments.ToJsonString())));
}
