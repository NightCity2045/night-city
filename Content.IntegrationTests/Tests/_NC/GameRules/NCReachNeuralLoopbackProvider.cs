// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Shared._NC.LivingCity.Neural;

namespace Content.IntegrationTests.Tests._NC.GameRules;

/// <summary>
/// Deterministic HTTP provider used only by the Reach integration evaluation. It deliberately exercises the same
/// serialization, status classification and response validation boundary as the production cognition gateway.
/// </summary>
internal sealed class NCReachNeuralLoopbackProvider : IAsyncDisposable
{
    public const string ApiKey = "reach-loopback-key";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ActorState> _actors = [];
    private readonly List<Guid> _actionOrder = [];
    private readonly Task _acceptLoop;
    private Guid? _rejectedActor;
    private int _providerSequence;

    public NCReachNeuralLoopbackProvider()
    {
        _listener.Start();
        var port = ((IPEndPoint) _listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/v1/cognition");
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    public NCReachNeuralLoopbackSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NCReachNeuralLoopbackSnapshot(
                _actors.ToDictionary(
                    pair => pair.Key,
                    pair => new NCReachNeuralLoopbackActorSnapshot(
                        pair.Value.Requests,
                        pair.Value.Rejections,
                        pair.Value.ActionRequestId,
                        pair.Value.WaitDecisions,
                        pair.Value.PostTerminalDecisions)),
                _actionOrder.ToArray(),
                _rejectedActor);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_handlers.ToArray());
        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }

            _handlers.Add(HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        try
        {
            var reader = new BufferedNetworkReader(stream);
            var requestLine = await reader.ReadLineAsync(_cancellation.Token);
            if (requestLine != "POST /v1/cognition HTTP/1.1")
            {
                await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, null);
                return;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(_cancellation.Token) is { } line && line.Length != 0)
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            if (!headers.TryGetValue("Authorization", out var authorization) ||
                authorization != $"Bearer {ApiKey}")
            {
                await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, null);
                return;
            }

            byte[] payload;
            if (headers.TryGetValue("Content-Length", out var lengthValue) &&
                int.TryParse(lengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var length) &&
                length >= 0)
            {
                payload = await reader.ReadExactlyAsync(length, _cancellation.Token);
            }
            else if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                     transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                payload = await reader.ReadChunkedAsync(_cancellation.Token);
            }
            else
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, null);
                return;
            }

            using var document = JsonDocument.Parse(payload);
            if (!TryReadRequest(document.RootElement, out var request))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, null);
                return;
            }

            NCNeuralResponseEnvelope? response;
            var status = HttpStatusCode.OK;
            lock (_sync)
            {
                if (!_actors.TryGetValue(request.ActorId, out var state))
                {
                    state = new ActorState();
                    _actors.Add(request.ActorId, state);
                }
                state.Requests++;

                // A single actor-local 422 proves malformed/rejected requests do not open the global circuit.
                if (_rejectedActor == null)
                {
                    _rejectedActor = request.ActorId;
                    state.Rejections++;
                    response = null;
                    status = HttpStatusCode.UnprocessableEntity;
                }
                else if (state.ActionRequestId == null)
                {
                    if (request.WorkplaceHandle == null || !request.HasWorkplaceOpportunity ||
                        !request.AllowedCapabilities.Contains("NCCognitionMoveToStationDestination"))
                    {
                        state.Rejections++;
                        response = null;
                        status = HttpStatusCode.UnprocessableEntity;
                    }
                    else
                    {
                        state.ActionRequestId = request.RequestId;
                        _actionOrder.Add(request.ActorId);
                        response = CreateActionResponse(request, ++_providerSequence);
                    }
                }
                else
                {
                    state.WaitDecisions++;
                    if (state.ActionRequestId is { } actionRequestId &&
                        request.TerminalRequestIds.Contains(actionRequestId))
                    {
                        state.PostTerminalDecisions++;
                    }
                    response = CreateWaitResponse(request, ++_providerSequence);
                }
            }

            await WriteResponseAsync(stream, status, response);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                await WriteResponseAsync(stream, HttpStatusCode.InternalServerError, null);
            }
            catch
            {
            }
        }
    }

    private static NCNeuralResponseEnvelope CreateActionResponse(LoopbackRequest request, int sequence)
    {
        var target = request.WorkplaceHandle!;
        return new NCNeuralResponseEnvelope(
            request.RequestId,
            request.SchemaVersion,
            request.ContextVersion,
            [
                new NCNeuralCommandProposal(
                    "NCCognitionMoveToStationDestination",
                    "execute",
                    target,
                    700,
                    "assigned-workplace-reached",
                    [],
                    []),
            ],
            "statement",
            string.Empty,
            false,
            "reach-workplace-action",
            900,
            12,
            0,
            8,
            $"reach-loopback-{sequence}",
            "gpt-5.6-luna",
            request.PromptStableKey,
            request.PromptVersion,
            request.EvaluationProfile,
            [],
            [])
        {
            DecisionMode = "act",
            Intent = new NCNeuralIntentChoice("report-to-assigned-workplace", target, 700),
            ReasonTags = ["assigned-workplace-opportunity"],
        };
    }

    private static NCNeuralResponseEnvelope CreateWaitResponse(LoopbackRequest request, int sequence) =>
        new(
            request.RequestId,
            request.SchemaVersion,
            request.ContextVersion,
            [],
            "statement",
            string.Empty,
            false,
            "reach-workplace-complete",
            900,
            8,
            0,
            4,
            $"reach-loopback-{sequence}",
            "gpt-5.6-luna",
            request.PromptStableKey,
            request.PromptVersion,
            request.EvaluationProfile,
            [],
            [])
        {
            DecisionMode = "wait",
            ReasonTags = ["assigned-workplace-complete"],
        };

    private static bool TryReadRequest(JsonElement root, out LoopbackRequest request)
    {
        request = default;
        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
            schemaVersion != NCNeuralContract.SchemaVersion ||
            !root.TryGetProperty("promptStableKey", out var promptStableKeyElement) ||
            promptStableKeyElement.GetString() is not { Length: > 0 } promptStableKey ||
            !root.TryGetProperty("promptVersion", out var promptVersionElement) ||
            !promptVersionElement.TryGetInt32(out var promptVersion) || promptVersion <= 0 ||
            !root.TryGetProperty("evaluationProfile", out var evaluationProfileElement) ||
            evaluationProfileElement.GetString() is not { Length: > 0 } evaluationProfile ||
            !root.TryGetProperty("requestId", out var requestIdElement) ||
            !requestIdElement.TryGetGuid(out var requestId) ||
            !root.TryGetProperty("actorId", out var actorElement) ||
            !actorElement.TryGetProperty("value", out var actorIdElement) ||
            !actorIdElement.TryGetGuid(out var actorId) ||
            !root.TryGetProperty("contextVersion", out var contextVersionElement) ||
            !contextVersionElement.TryGetInt64(out var contextVersion) ||
            !root.TryGetProperty("allowedCapabilityIds", out var capabilitiesElement) ||
            capabilitiesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (!root.TryGetProperty("perceptionDeltas", out var perceptionDeltas) ||
            perceptionDeltas.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("perceivedSubjects", out var perceivedSubjects) ||
            perceivedSubjects.ValueKind != JsonValueKind.Array ||
            perceivedSubjects.EnumerateArray().Any(subject =>
                !subject.TryGetProperty("identityKnown", out var identityKnown) ||
                identityKnown.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
        {
            return false;
        }

        var capabilities = capabilitiesElement.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        string? workplaceHandle = null;
        if (root.TryGetProperty("knownLocations", out var locationsElement) &&
            locationsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locationsElement.EnumerateArray())
            {
                if (!location.TryGetProperty("kind", out var kind) || kind.GetString() != "assigned-workplace" ||
                    !location.TryGetProperty("handle", out var handle))
                {
                    continue;
                }
                workplaceHandle = handle.GetString();
                break;
            }
        }

        var hasWorkplaceOpportunity = false;
        if (workplaceHandle != null &&
            root.TryGetProperty("worldOpportunities", out var opportunitiesElement) &&
            opportunitiesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var opportunity in opportunitiesElement.EnumerateArray())
            {
                if (!opportunity.TryGetProperty("targetHandle", out var target) ||
                    target.GetString() != workplaceHandle ||
                    !opportunity.TryGetProperty("affordanceKeys", out var affordances) ||
                    affordances.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                hasWorkplaceOpportunity = affordances.EnumerateArray()
                    .Any(value => value.GetString() == "task.report-to-assigned-workplace");
                if (hasWorkplaceOpportunity)
                    break;
            }
        }

        var terminalRequestIds = new HashSet<Guid>();
        if (root.TryGetProperty("recentCommandResults", out var resultsElement) &&
            resultsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in resultsElement.EnumerateArray())
            {
                if (!result.TryGetProperty("requestId", out var resultRequestId) ||
                    !resultRequestId.TryGetGuid(out var terminalRequestId) ||
                    !result.TryGetProperty("state", out var state) ||
                    state.GetString() is not ("completed" or "failed"))
                {
                    continue;
                }
                terminalRequestIds.Add(terminalRequestId);
            }
        }

        request = new LoopbackRequest(
            schemaVersion,
            requestId,
            actorId,
            contextVersion,
            promptStableKey,
            promptVersion,
            evaluationProfile,
            capabilities,
            workplaceHandle,
            hasWorkplaceOpportunity,
            terminalRequestIds);
        return true;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode status,
        NCNeuralResponseEnvelope? response)
    {
        var payload = response == null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var reason = status switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.UnprocessableEntity => "Unprocessable Entity",
            _ => "Internal Server Error",
        };
        var contentType = payload.Length == 0 ? string.Empty : "Content-Type: application/json\r\n";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int) status} {reason}\r\n{contentType}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        if (payload.Length != 0)
            await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    /// <summary>
    /// Minimal HTTP/1.1 reader for the loopback fixture. Supporting both body encodings keeps the fake provider on
    /// the real HttpClient serialization path without depending on the Windows HTTP listener service.
    /// </summary>
    private sealed class BufferedNetworkReader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            while (line.Length <= 65536)
            {
                var value = await ReadByteAsync(cancellationToken);
                if (value < 0)
                    return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                if (value == '\n')
                    return Encoding.ASCII.GetString(line.ToArray());
                if (value != '\r')
                    line.WriteByte((byte) value);
            }

            throw new InvalidDataException("HTTP header line exceeded the test provider limit.");
        }

        public async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
        {
            var result = new byte[length];
            var written = 0;
            while (written < length)
            {
                if (_offset == _count)
                    await FillAsync(cancellationToken);
                var copy = Math.Min(length - written, _count - _offset);
                if (copy == 0)
                    throw new EndOfStreamException();
                _buffer.AsSpan(_offset, copy).CopyTo(result.AsSpan(written));
                _offset += copy;
                written += copy;
            }
            return result;
        }

        public async Task<byte[]> ReadChunkedAsync(CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
                var extension = sizeLine.IndexOf(';');
                var sizeText = extension < 0 ? sizeLine : sizeLine[..extension];
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) ||
                    size < 0)
                {
                    throw new InvalidDataException("Invalid HTTP chunk length.");
                }

                if (size == 0)
                {
                    while (await ReadLineAsync(cancellationToken) is { Length: > 0 })
                    {
                    }
                    return body.ToArray();
                }

                var chunk = await ReadExactlyAsync(size, cancellationToken);
                await body.WriteAsync(chunk, cancellationToken);
                if (await ReadLineAsync(cancellationToken) != string.Empty)
                    throw new InvalidDataException("Invalid HTTP chunk terminator.");
            }
        }

        private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_offset == _count)
                await FillAsync(cancellationToken);
            return _offset == _count ? -1 : _buffer[_offset++];
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            _offset = 0;
            _count = await stream.ReadAsync(_buffer, cancellationToken);
        }
    }

    private sealed record LoopbackRequest(
        int SchemaVersion,
        Guid RequestId,
        Guid ActorId,
        long ContextVersion,
        string PromptStableKey,
        int PromptVersion,
        string EvaluationProfile,
        IReadOnlySet<string> AllowedCapabilities,
        string? WorkplaceHandle,
        bool HasWorkplaceOpportunity,
        IReadOnlySet<Guid> TerminalRequestIds);

    private sealed class ActorState
    {
        public int Requests;
        public int Rejections;
        public Guid? ActionRequestId;
        public int WaitDecisions;
        public int PostTerminalDecisions;
    }
}

internal sealed record NCReachNeuralLoopbackSnapshot(
    IReadOnlyDictionary<Guid, NCReachNeuralLoopbackActorSnapshot> Actors,
    IReadOnlyList<Guid> ActionOrder,
    Guid? RejectedActor);

internal sealed record NCReachNeuralLoopbackActorSnapshot(
    int Requests,
    int Rejections,
    Guid? ActionRequestId,
    int WaitDecisions,
    int PostTerminalDecisions);
