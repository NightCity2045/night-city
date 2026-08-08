// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Preferences;

/// <summary>
/// Server-owned employment projected to the lobby by character slot.
/// Missing slots have no employment record; clients never use this message as authority.
/// </summary>
public sealed class NCEmploymentSnapshotMessage : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    /// <summary>
    /// Slots with a persistent employment record. A null job means the record is terminated.
    /// Slots absent from this dictionary have never been employed and may choose an entry department.
    /// </summary>
    public Dictionary<int, string?> Employment = new();

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Employment.Clear();
        var count = buffer.ReadVariableInt32();
        for (var index = 0; index < count; index++)
        {
            var slot = buffer.ReadVariableInt32();
            Employment[slot] = buffer.ReadBoolean() ? buffer.ReadString() : null;
        }
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.WriteVariableInt32(Employment.Count);
        foreach (var (slot, job) in Employment)
        {
            buffer.WriteVariableInt32(slot);
            buffer.Write(job != null);
            if (job != null)
                buffer.Write(job);
        }
    }
}

/// <summary>
/// Requests voluntary termination of the authenticated user's selected character employment.
/// The request intentionally contains no character identifier that a client could forge.
/// </summary>
public sealed class NCResignEmploymentMessage : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
    }
}
