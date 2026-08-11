// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Database._NC.Police;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Watchlist;

/// <summary>
/// Data-only settings for the read-only persistent NCPD watchlist feed.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentWatchlistComponent : Component
{
    [DataField, AutoNetworkedField]
    public int ResultLimit = 100;
}

[Serializable, NetSerializable]
public sealed record NCAgentWatchlistRecord(
    int CharacterId,
    string CharacterName,
    string? JobPrototypeId,
    NCPoliceOperationalStatus Status,
    string? Reason,
    string UpdatedByName,
    DateTime? UpdatedAt);

[Serializable, NetSerializable]
public sealed class NCAgentWatchlistUiState(List<NCAgentWatchlistRecord> records) : BoundUserInterfaceState
{
    public readonly List<NCAgentWatchlistRecord> Records = records;
}
