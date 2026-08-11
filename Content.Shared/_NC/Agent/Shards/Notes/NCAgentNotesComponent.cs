// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Notes;

/// <summary>
/// Device-local structured note storage. The component contains data only; all mutations are server-authoritative.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentNotesComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<NCAgentNoteEntry> Entries = [];

    [DataField, AutoNetworkedField]
    public int MaximumNotes = 64;

    [DataField, AutoNetworkedField]
    public int MaximumNoteLength = 512;
}

/// <summary>
/// Stable note identity allows edits and deletion without relying on duplicate-prone body text.
/// </summary>
[Serializable, NetSerializable, DataRecord]
public sealed partial class NCAgentNoteEntry
{
    public readonly Guid Id;
    public readonly string Body;
    public readonly DateTime CreatedAt;
    public readonly DateTime UpdatedAt;

    public NCAgentNoteEntry(Guid id, string body, DateTime createdAt, DateTime updatedAt)
    {
        Id = id;
        Body = body;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }
}
