// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

public sealed class NCNcpdOfficerProjectedEvent(
    NCActorId actorId,
    ProtoId<NCNcpdRoleProfilePrototype> roleProfile) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public ProtoId<NCNcpdRoleProfilePrototype> RoleProfile { get; } = roleProfile;
}

public sealed class NCNcpdDutyChangedEvent(
    NCActorId actorId,
    NCNcpdDutyStatus oldStatus,
    NCNcpdDutyStatus newStatus) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCNcpdDutyStatus OldStatus { get; } = oldStatus;
    public NCNcpdDutyStatus NewStatus { get; } = newStatus;
}

public sealed class NCNcpdCommandChangedEvent(
    NCNcpdCommandId commandId,
    NCNcpdUnitId unitId,
    NCNcpdCommandStatus status) : EntityEventArgs
{
    public NCNcpdCommandId CommandId { get; } = commandId;
    public NCNcpdUnitId UnitId { get; } = unitId;
    public NCNcpdCommandStatus Status { get; } = status;
}

/// <summary>Signals that an already-authorized dispatch order was projected onto a physical officer.</summary>
public sealed class NCNcpdAssignmentAppliedEvent(
    EntityUid entityUid,
    NCNcpdCommandId commandId) : EntityEventArgs
{
    public EntityUid EntityUid { get; } = entityUid;
    public NCNcpdCommandId CommandId { get; } = commandId;
}

/// <summary>Raised only after vanilla pulling accepted a restrained suspect.</summary>
public sealed class NCNcpdEscortStartedEvent(
    EntityUid officer,
    EntityUid suspect,
    NCCityIncidentId incidentId,
    NCActiveWorldTime startedAt) : EntityEventArgs
{
    public EntityUid Officer { get; } = officer;
    public EntityUid Suspect { get; } = suspect;
    public NCCityIncidentId IncidentId { get; } = incidentId;
    public NCActiveWorldTime StartedAt { get; } = startedAt;
}

public sealed class NCNcpdUnitMembershipAppliedEvent(
    NCActorId actorId,
    EntityUid entityUid,
    NCNcpdUnitId unitId) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public NCNcpdUnitId UnitId { get; } = unitId;
}

public sealed class NCNcpdUnitRemovedEvent(NCNcpdUnitId unitId, NCActorId[] members) : EntityEventArgs
{
    public NCNcpdUnitId UnitId { get; } = unitId;
    public NCActorId[] Members { get; } = members;
}

public sealed record NCNcpdDutySnapshot(
    NCActorId ActorId,
    ProtoId<NCNcpdRoleProfilePrototype> RoleProfile,
    NCNcpdDutyStatus Status);

public sealed record NCNcpdUnitSnapshot(
    NCNcpdUnitId UnitId,
    NCActorId LeaderActorId,
    NCActorId[] Members);

public sealed record NCNcpdServiceDispatchAdapterSnapshot(
    NCServiceDispatchId DispatchId,
    NCCityIncidentId IncidentId,
    NCServiceDispatchStatus ServiceStatus,
    NCNcpdUnitId UnitId,
    NCNcpdCommandId CommandId,
    string AdapterStatus,
    string Failure,
    NCActiveWorldTime UpdatedAt);

/// <summary>Signals that an authoritative investigation dispatch has selected its real NCPD unit.</summary>
public sealed class NCNcpdInvestigationUnitAssignedEvent(
    NCCityIncidentId incidentId,
    NCNcpdUnitId unitId) : EntityEventArgs
{
    public NCCityIncidentId IncidentId { get; } = incidentId;
    public NCNcpdUnitId UnitId { get; } = unitId;
}

public sealed record NCNcpdMappingDiagnostics(
    bool StationValid,
    int StationAffordances,
    int PatrolAreas,
    string[] Errors);

public sealed record NCNcpdCommandRecord(
    NCNcpdCommandId CommandId,
    NCNcpdUnitId UnitId,
    NCNcpdCommandKind Kind,
    NCZoneId TargetZone,
    int Priority,
    NCCharacterId IssuerCharacterId,
    string IssuerName,
    ProtoId<JobPrototype> IssuerJob,
    string Reason,
    NCNcpdCommandStatus Status,
    NCActiveWorldTime IssuedAt,
    string Note);

public sealed record NCNcpdIssuerContext(
    NCCharacterId CharacterId,
    ProtoId<JobPrototype> Job,
    string Name,
    int Rank);
