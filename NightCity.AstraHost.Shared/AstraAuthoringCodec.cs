// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using AstraGraph.Editor.Protocol;

namespace Content.Shared._NC.AstraGraph;

public static class AstraAuthoringCodec
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    public static byte[] Serialize(AuthoringMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), AuthoringJsonContext.Default);
    }

    public static AuthoringMessage? Deserialize(string kind, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(kind) || payload.Length > MaxPayloadBytes)
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
            "ui.save.request" => JsonSerializer.Deserialize<UiSaveRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.save.response" => JsonSerializer.Deserialize<UiSaveResponseMsg>(payload, AuthoringJsonContext.Default),
            "ui.list.request" => JsonSerializer.Deserialize<UiListRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.list.response" => JsonSerializer.Deserialize<UiListResponseMsg>(payload, AuthoringJsonContext.Default),
            "ui.catalog.request" => JsonSerializer.Deserialize<UiCatalogRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.catalog.response" => JsonSerializer.Deserialize<UiCatalogResponseMsg>(payload, AuthoringJsonContext.Default),
            "ui.preview.request" => JsonSerializer.Deserialize<UiPreviewRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.preview.response" => JsonSerializer.Deserialize<UiPreviewResponseMsg>(payload, AuthoringJsonContext.Default),
            "ui.patch.request" => JsonSerializer.Deserialize<UiPatchRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.compile.request" => JsonSerializer.Deserialize<UiCompileRequestMsg>(payload, AuthoringJsonContext.Default),
            "ui.compile.response" => JsonSerializer.Deserialize<UiCompileResponseMsg>(payload, AuthoringJsonContext.Default),
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
