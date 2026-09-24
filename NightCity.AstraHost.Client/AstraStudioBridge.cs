// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using AstraGraph.Editor.Protocol;
using AstraGraph.Robust.Client;
using Content.Shared._NC.AstraGraph;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Client._NC.AstraGraph;

public interface IAstraAuthoringTransport
{
    Task<AstraWireReply?> SendAsync(string kind, byte[] payload, CancellationToken cancellation);
}

/// <summary>
/// Opens Studio and forwards authoring. Content supplies the net transport.
/// </summary>
public static class AstraStudioBridge
{
    public static Task OpenAsync(string token, IAstraAuthoringTransport transport)
    {
        var proxy = new ServerProxy(transport, token);
        var systems = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<ClientAstraGraphSystem>();
        return systems.LaunchStudioAsync(proxy);
    }

    private sealed class ServerProxy : IAuthoringMessageHandler
    {
        private readonly IAstraAuthoringTransport _transport;

        public ServerProxy(IAstraAuthoringTransport transport, string token)
        {
            _transport = transport;
            Token = token;
        }

        public string Token { get; }

        public async Task<AuthoringMessage?> HandleAsync(AuthoringMessage message, CancellationToken cancellation)
        {
            if (message is UiPreviewRequestMsg preview)
                AstraHtmlGameWindow.Apply(preview);

            if (message is AuthHandshakeRequestMsg handshake && string.IsNullOrEmpty(handshake.AuthorToken))
            {
                message = new AuthHandshakeRequestMsg
                {
                    MessageId = handshake.MessageId,
                    Timestamp = handshake.Timestamp,
                    ClientVersion = handshake.ClientVersion,
                    AuthorName = handshake.AuthorName,
                    AuthorToken = Token
                };
            }

            var reply = await _transport.SendAsync(message.Kind, AstraAuthoringCodec.Serialize(message), cancellation);
            if (reply == null)
                return null;

            return AstraAuthoringCodec.Deserialize(reply.Value.Kind, reply.Value.Payload);
        }
    }
}
