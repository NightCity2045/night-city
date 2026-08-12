// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Diagnostics;

/// <summary>
/// Sanitized owner HUD value. It contains neither wound identifiers nor internal medical measurements.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCMedicalZoneStatus(
    ProtoId<NCTargetZonePrototype> Zone,
    byte Severity);

/// <summary>
/// Requests the current body-status doll for the entity controlled by the sending session.
/// The client cannot supply or inspect another entity identifier.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCMedicalStatusSnapshotRequest(uint requestId) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
}

/// <summary>
/// Directed owner-only response used by the targeting doll. Detailed wounds remain server-only.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCMedicalStatusSnapshotResponse(
    uint requestId,
    bool accepted,
    uint revision,
    List<NCMedicalZoneStatus> zones) : EntityEventArgs
{
    public uint RequestId { get; } = requestId;
    public bool Accepted { get; } = accepted;
    public uint Revision { get; } = revision;
    public List<NCMedicalZoneStatus> Zones { get; } = zones;
}
