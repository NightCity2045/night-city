// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Describes why the server rejected a targeting-mode change.
/// </summary>
[Serializable, NetSerializable]
public enum NCTargetingSelectionFailure : byte
{
    None,
    NoControlledEntity,
    InvalidMode,
    AnatomyUnavailable,
    AnatomyIncomplete,
    ZoneRequired,
    ZoneNotInProfile,
    ZonePrototypeUnavailable,
}

/// <summary>
/// Requests a targeting-mode change for the entity controlled by the sending session.
/// The controlled entity is deliberately absent: the server derives it from the network session.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCTargetingSelectionRequest(
    uint requestId,
    NCTargetingState state) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public NCTargetingState State { get; } = state;
}

/// <summary>
/// Requests the existing private selection when a targeting HUD is opened or rebuilt.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCTargetingSelectionQuery(uint requestId) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
}

/// <summary>
/// Directed acknowledgement containing the requester's current authoritative selection.
/// This event must only be sent to the session that issued the matching request.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCTargetingSelectionAck(
    uint requestId,
    bool accepted,
    NCTargetingState state,
    NCTargetingSelectionFailure failure) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public bool Accepted { get; } = accepted;
    public NCTargetingState State { get; } = state;
    public NCTargetingSelectionFailure Failure { get; } = failure;
}
