// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Notes;

[Serializable, NetSerializable]
public sealed class NCAgentNotesUiState(List<NCAgentNoteEntry> entries) : BoundUserInterfaceState
{
    public readonly List<NCAgentNoteEntry> Entries = entries;
}

[Serializable, NetSerializable]
public sealed class NCAgentNotesUiMessage(
    NCAgentNotesAction action,
    Guid noteId,
    string body) : CartridgeMessageEvent
{
    public readonly NCAgentNotesAction Action = action;
    public readonly Guid NoteId = noteId;
    public readonly string Body = body;
}

[Serializable, NetSerializable]
public enum NCAgentNotesAction : byte
{
    Add,
    Edit,
    Delete,
}
