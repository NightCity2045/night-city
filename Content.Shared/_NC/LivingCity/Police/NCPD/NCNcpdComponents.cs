// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
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
    [ViewVariables(VVAccess.ReadOnly)] public Guid SourceWorldEventId;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public string SuspectHandle = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime ReportedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdIncidentReportStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Round-local factual claim held by one officer about one currently observed suspect. It never grants authority to
/// mutate the suspect; physical systems still confirm speech, movement, combat and restraints.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdSuspectClaimComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid SourceWorldEventId;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid Suspect;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef SuspectParty;
    [ViewVariables(VVAccess.ReadOnly)] public string SuspectHandle = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public string Basis = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdSuspectStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdForceLevel ForceLevel;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime ClaimedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime? OrderedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime? ResponseDeadline;
    [ViewVariables(VVAccess.ReadOnly)] public MapId OrderMap;
    [ViewVariables(VVAccess.ReadOnly)] public Vector2 OrderPosition;
    [ViewVariables(VVAccess.ReadOnly)] public string ResultReason = string.Empty;
}

/// <summary>
/// Observable round projection of a real detention. Persistent actor/player records remain authoritative.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdCustodyComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid BookingId;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef Detainee;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef ArrestingOfficer;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdCustodyStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Bounded round-local result of physically searching one restrained suspect. Entity references are projections;
/// the incident registry remains authoritative for evidence that has actually entered officer custody.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdEvidenceSearchComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid Suspect;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef SuspectParty;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdEvidenceSearchStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public int InspectedItems;
    [ViewVariables(VVAccess.ReadOnly)] public List<EntityUid> DangerousItems = [];
    [ViewVariables(VVAccess.ReadOnly)] public List<EntityUid> SeizedItems = [];
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Physical chain-of-custody marker for a seized item. It never substitutes for the item being present in the
/// officer's real hands or storage and is persisted only after that physical transfer succeeds.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdEvidenceItemComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid EvidenceId;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef SeizedFrom;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef SeizedBy;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdEvidenceCustodyStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Observable physical handoff of an officer's seized items into a real evidence storage entity.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdEvidenceSubmissionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdEvidenceSubmissionStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public NCMapCoordinates Destination;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid Receiver;
    [ViewVariables(VVAccess.ReadOnly)] public int SubmittedItems;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Observable projection of a physical witness interview. The persistent incident and the witness's own structured
/// memory remain authoritative; this component only exposes the current field-work state.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdWitnessInterviewComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public Guid SourceWorldEventId;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdWitnessInterviewStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public int Candidates;
    [ViewVariables(VVAccess.ReadOnly)] public int RecordedStatements;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid CurrentWitness = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>Round-local projection of a canonical cross-party wanted record.</summary>
[RegisterComponent]
public sealed partial class NCNcpdWantedProjectionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCPoliceWantedId WantedId;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public NCPartyRef Subject;
    [ViewVariables(VVAccess.ReadOnly)] public NCPoliceWantedStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public bool Submitting;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
}

/// <summary>
/// Round-local projection of one persistent incident assigned to a physical investigator. The incident aggregate,
/// its evidence and wanted records remain authoritative.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdInvestigationComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public NCCityIncidentId IncidentId;
    [ViewVariables(VVAccess.ReadOnly)] public Guid SourceWorldEventId;
    [ViewVariables(VVAccess.ReadOnly)] public long IncidentVersion;
    [ViewVariables(VVAccess.ReadOnly)] public NCNcpdInvestigationStatus Status;
    [ViewVariables(VVAccess.ReadOnly)] public int EvidenceCount;
    [ViewVariables(VVAccess.ReadOnly)] public int WitnessCount;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime UpdatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public string Failure = string.Empty;
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
    [ViewVariables(VVAccess.ReadOnly)] public bool HasExecutionTarget;
    [ViewVariables(VVAccess.ReadOnly)] public NCMapCoordinates ExecutionTarget;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime NextEvaluationAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime? ReachedAt;
    [ViewVariables(VVAccess.ReadOnly)] public int Replans;
    [ViewVariables(VVAccess.ReadOnly)] public uint RouteCycle;
    [ViewVariables(VVAccess.ReadOnly)] public string ExecutionNote = string.Empty;
}

/// <summary>
/// Data-only configuration for the stationary NCPD operations console.
/// </summary>
[RegisterComponent]
public sealed partial class NCNcpdOperationsConsoleComponent : Component
{
    [DataField] public ProtoId<NCNcpdCommandPolicyPrototype> Policy = "NCDefaultNcpdCommandPolicy";
}
