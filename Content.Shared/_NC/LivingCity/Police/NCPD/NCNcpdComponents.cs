// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Police.NCPD;

/// <summary>
/// Physical, non-authoritative projection of a persistent NCPD employment relation.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdOfficerComponent : Component
{
    [DataField] public NCActorId ActorId;
    [DataField] public ProtoId<NCNcpdRoleProfilePrototype> RoleProfile;
    [DataField] public NCZoneId StationZone;
}

/// <summary>
/// Round-local duty state restored from the actor schedule whenever an officer materializes.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdDutyComponent : Component
{
    [DataField] public NCNcpdDutyStatus Status;
    [DataField] public NCZoneId StationZone;
}

/// <summary>
/// Round-local membership in a stable police unit. Unit authority remains in NCNcpdUnitSystem.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdUnitMemberComponent : Component
{
    [DataField] public NCNcpdUnitId UnitId;
    [DataField] public NCActorId LeaderActorId;
    [DataField] public NCNcpdUnitRole Role;
}

/// <summary>
/// Declares that the NCPD behavior branch currently owns physical decisions for this entity.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdPoliceClaimComponent : Component
{
    [DataField] public Guid AssignmentId;
}

/// <summary>
/// Round-local projection of the incident an officer has reported. Dispatch remains authoritative over assignments.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdIncidentReportComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid ReportId;
    [ViewVariables(VVAccess.ReadOnly)] public string SuspectHandle = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime ReportedAt;
}

/// <summary>
/// Round-local physical projection of the server-owned command assigned to an NCPD unit.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdAssignmentComponent : Component
{
    [DataField] public NCNcpdCommandId CommandId;
    [DataField] public NCNcpdCommandKind Kind;
    [DataField] public NCZoneId TargetZone;
    [DataField] public NCNcpdCommandStatus Status;
    [DataField] public int Priority;
}

/// <summary>
/// Data-only configuration for the stationary NCPD operations console.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdOperationsConsoleComponent : Component
{
    [DataField] public ProtoId<NCNcpdCommandPolicyPrototype> Policy = "NCDefaultNcpdCommandPolicy";
}
