// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Mapping;

/// <summary>
/// Requests an administrative move to a depth in the player's current Z-network.
/// The server derives the network from the attached entity instead of trusting client entity identifiers.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCMappingFloorChangeRequest(int targetDepth) : EntityEventArgs
{
    public int TargetDepth { get; } = targetDepth;
}

/// <summary>
/// Reports the authoritative result of a mapper floor change.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCMappingFloorChangeResult(bool success, string message, int currentDepth) : EntityEventArgs
{
    public bool Success { get; } = success;
    public string Message { get; } = message;
    public int CurrentDepth { get; } = currentDepth;
}
