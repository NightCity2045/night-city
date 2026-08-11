// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Directory;

/// <summary>
/// Data-only settings for the public persistent Night City personnel directory.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentDirectoryComponent : Component
{
    [DataField, AutoNetworkedField]
    public int ResultLimit = 100;
}

[Serializable, NetSerializable]
public sealed record NCAgentDirectoryRecord(
    int CharacterId,
    string CharacterName,
    string JobPrototypeId,
    string JobTitle,
    string Organization,
    DateTime StartedAt);

[Serializable, NetSerializable]
public sealed class NCAgentDirectoryUiState(List<NCAgentDirectoryRecord> records) : BoundUserInterfaceState
{
    public readonly List<NCAgentDirectoryRecord> Records = records;
}
