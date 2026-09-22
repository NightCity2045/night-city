// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
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
