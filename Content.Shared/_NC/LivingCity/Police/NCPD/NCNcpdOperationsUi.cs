// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Agent.UI;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Police.NCPD;

[Serializable, NetSerializable]
public enum NCNcpdOperationsUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed record NCNcpdOperationsUnitSummary(
    Guid UnitId,
    string Callsign,
    string Leader,
    string Members,
    string CurrentZone,
    string Assignment,
    bool Commandable);

[Serializable, NetSerializable]
public sealed record NCNcpdOperationsZoneSummary(
    Guid ZoneId,
    string Name,
    string ParentName,
    string Scope);

[Serializable, NetSerializable]
public sealed record NCNcpdOperationsCommandSummary(
    Guid CommandId,
    Guid UnitId,
    string Callsign,
    NCNcpdCommandKind Kind,
    string Zone,
    int Priority,
    string Issuer,
    string Reason,
    NCNcpdCommandStatus Status,
    TimeSpan IssuedAt,
    string Note,
    bool Cancellable);

/// <summary>
/// Bounded per-user console snapshot. It contains sanitized cartography, never mutable city-zone prototypes.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCNcpdOperationsUpdateMessage(
    string authorityJob,
    string authorityName,
    bool canIssue,
    string notice,
    List<NCNcpdCommandKind> allowedCommands,
    List<NCNcpdOperationsUnitSummary> units,
    List<NCNcpdOperationsZoneSummary> zones,
    List<NCNcpdOperationsCommandSummary> commands,
    string networkId,
    int z,
    Vector2 consolePosition,
    List<NCAgentMapFeature> mapFeatures) : BoundUserInterfaceMessage
{
    public readonly string AuthorityJob = authorityJob;
    public readonly string AuthorityName = authorityName;
    public readonly bool CanIssue = canIssue;
    public readonly string Notice = notice;
    public readonly List<NCNcpdCommandKind> AllowedCommands = allowedCommands;
    public readonly List<NCNcpdOperationsUnitSummary> Units = units;
    public readonly List<NCNcpdOperationsZoneSummary> Zones = zones;
    public readonly List<NCNcpdOperationsCommandSummary> Commands = commands;
    public readonly string NetworkId = networkId;
    public readonly int Z = z;
    public readonly Vector2 ConsolePosition = consolePosition;
    public readonly List<NCAgentMapFeature> MapFeatures = mapFeatures;
}

[Serializable, NetSerializable]
public sealed class NCNcpdOperationsIssueMessage(
    Guid unitId,
    NCNcpdCommandKind kind,
    Guid targetZone,
    int priority,
    string reason) : BoundUserInterfaceMessage
{
    public readonly Guid UnitId = unitId;
    public readonly NCNcpdCommandKind Kind = kind;
    public readonly Guid TargetZone = targetZone;
    public readonly int Priority = priority;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCNcpdOperationsCancelMessage(Guid commandId, string reason) : BoundUserInterfaceMessage
{
    public readonly Guid CommandId = commandId;
    public readonly string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class NCNcpdOperationsRefreshMessage : BoundUserInterfaceMessage;
