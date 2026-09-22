// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Text.Json;
using AstraGraph.Editor.Protocol;
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Asks the server for a Studio launch token. The browser never connects to the game port.
/// </summary>
public sealed class MsgAstraStudioLaunch : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    public int RequestId;
    public bool IsResponse;
    public bool Granted;
    public string Token = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        RequestId = buffer.ReadInt32();
        IsResponse = buffer.ReadBoolean();
        Granted = buffer.ReadBoolean();
        Token = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(RequestId);
        buffer.Write(IsResponse);
        buffer.Write(Granted);
        buffer.Write(Token);
    }
}

/// <summary>
/// One authoring request or response. The local browser bridge stays on the client.
/// </summary>
public sealed class MsgAstraAuthoring : NetMessage
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    public override MsgGroups MsgGroup => MsgGroups.Command;

    public int RequestId;
    public bool IsResponse;
    public string Kind = string.Empty;
    public byte[] Payload = Array.Empty<byte>();

    public override int EstimateBufferSize() => 12 + Kind.Length + Payload.Length;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        RequestId = buffer.ReadInt32();
        IsResponse = buffer.ReadBoolean();
        Kind = buffer.ReadString();
        var length = buffer.ReadInt32();
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidOperationException($"AstraGraph authoring payload is {length} bytes.");

        Payload = length == 0 ? Array.Empty<byte>() : buffer.ReadBytes(length);
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        if (Payload.Length > MaxPayloadBytes)
            throw new InvalidOperationException($"AstraGraph authoring payload is {Payload.Length} bytes.");

        buffer.Write(RequestId);
        buffer.Write(IsResponse);
        buffer.Write(Kind);
        buffer.Write(Payload.Length);
        if (Payload.Length > 0)
            buffer.Write(Payload);
    }
}

public static class AstraAuthoringCodec
{
    public static byte[] Serialize(AuthoringMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), AuthoringJsonContext.Default);
    }

    public static AuthoringMessage? Deserialize(string kind, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(kind) || payload.Length > MsgAstraAuthoring.MaxPayloadBytes)
            return null;

        return kind switch
        {
            "auth.handshake.request" => JsonSerializer.Deserialize<AuthHandshakeRequestMsg>(payload, AuthoringJsonContext.Default),
            "auth.handshake.response" => JsonSerializer.Deserialize<AuthHandshakeResponseMsg>(payload, AuthoringJsonContext.Default),
            "graph.list.request" => JsonSerializer.Deserialize<GraphListRequestMsg>(payload, AuthoringJsonContext.Default),
            "graph.list.response" => JsonSerializer.Deserialize<GraphListResponseMsg>(payload, AuthoringJsonContext.Default),
            "draft.save.request" => JsonSerializer.Deserialize<DraftSaveRequestMsg>(payload, AuthoringJsonContext.Default),
            "draft.save.response" => JsonSerializer.Deserialize<DraftSaveResponseMsg>(payload, AuthoringJsonContext.Default),
            "draft.compile.request" => JsonSerializer.Deserialize<DraftCompileRequestMsg>(payload, AuthoringJsonContext.Default),
            "draft.compile.response" => JsonSerializer.Deserialize<DraftCompileResponseMsg>(payload, AuthoringJsonContext.Default),
            "draft.publish.request" => JsonSerializer.Deserialize<DraftPublishRequestMsg>(payload, AuthoringJsonContext.Default),
            "draft.publish.response" => JsonSerializer.Deserialize<DraftPublishResponseMsg>(payload, AuthoringJsonContext.Default),
            "graph.fetch.request" => JsonSerializer.Deserialize<GraphFetchRequestMsg>(payload, AuthoringJsonContext.Default),
            "graph.fetch.response" => JsonSerializer.Deserialize<GraphFetchResponseMsg>(payload, AuthoringJsonContext.Default),
            "catalog.query.request" => JsonSerializer.Deserialize<CatalogQueryRequestMsg>(payload, AuthoringJsonContext.Default),
            "catalog.query.response" => JsonSerializer.Deserialize<CatalogQueryResponseMsg>(payload, AuthoringJsonContext.Default),
            "history.list.request" => JsonSerializer.Deserialize<HistoryListRequestMsg>(payload, AuthoringJsonContext.Default),
            "history.list.response" => JsonSerializer.Deserialize<HistoryListResponseMsg>(payload, AuthoringJsonContext.Default),
            "history.rollback.request" => JsonSerializer.Deserialize<HistoryRollbackRequestMsg>(payload, AuthoringJsonContext.Default),
            "session.updated" => JsonSerializer.Deserialize<SessionUpdatedMsg>(payload, AuthoringJsonContext.Default),
            "debugger.command.request" => JsonSerializer.Deserialize<DebuggerCommandRequestMsg>(payload, AuthoringJsonContext.Default),
            "debugger.command.response" => JsonSerializer.Deserialize<DebuggerCommandResponseMsg>(payload, AuthoringJsonContext.Default),
            "debug.stream.event" => JsonSerializer.Deserialize<DebugStreamEventMsg>(payload, AuthoringJsonContext.Default),
            "profiler.stream.event" => JsonSerializer.Deserialize<ProfilerStreamEventMsg>(payload, AuthoringJsonContext.Default),
            "ping" => JsonSerializer.Deserialize<PingMsg>(payload, AuthoringJsonContext.Default),
            "pong" => JsonSerializer.Deserialize<PongMsg>(payload, AuthoringJsonContext.Default),
            "error" => JsonSerializer.Deserialize<ErrorMsg>(payload, AuthoringJsonContext.Default),
            _ => null
        };
    }
}
