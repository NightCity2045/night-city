// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.CityServices;

[Serializable, NetSerializable]
public readonly record struct NCCityIncidentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCCityIncidentKind : byte
{
    Crime,
    Violence,
    MedicalEmergency,
    Cyberpsychosis,
    FactionConflict,
    GangActivity,
    CorporateAction,
    LogisticsDisruption,
    PropertyEmergency,
    Fire,
    Accident,
    Disease,
    Shortage,
}

[Serializable, NetSerializable]
public enum NCCityIncidentStage : byte
{
    Reported,
    Verified,
    Dispatching,
    ResponseEnRoute,
    Active,
    Contained,
    Investigation,
    Resolved,
    Closed,
    Cancelled,
}

[Serializable, NetSerializable]
public enum NCIncidentParticipantRole : byte
{
    Subject,
    Victim,
    Suspect,
    Witness,
    Reporter,
    Responder,
    ResponsibleFaction,
}

[Serializable, NetSerializable]
public enum NCIncidentEvidenceKind : byte
{
    Physical,
    Medical,
    DeviceRecord,
    FinancialRecord,
    WeaponTrace,
    WitnessStatement,
    WorldEvent,
}

[Serializable, NetSerializable]
public enum NCIncidentMaterializationPolicy : byte
{
    // Retained for stored/API compatibility. In v3 none of these values permits an off-screen outcome.
    AbstractUnlessObserved,
    MaterializeForResponse,
    AlwaysPhysical,
}

public sealed record NCIncidentParticipant(NCPartyRef Party, NCIncidentParticipantRole Role, bool Confirmed);

public sealed record NCIncidentEvidence(Guid EvidenceId, NCIncidentEvidenceKind Kind, NCPartyRef SubmittedBy,
    string ReferenceId, int Reliability, bool Admissible, NCActiveWorldTime CreatedAt);

public sealed record NCIncidentWitnessStatement(Guid StatementId, NCPartyRef Witness, NCMemoryId? MemoryId,
    string StatementKey, int Confidence, IReadOnlyList<NCPartyRef> IdentifiedParties,
    NCActiveWorldTime CreatedAt);

public sealed record NCIncidentCause(Guid SourceWorldEventId, string Domain, string EventType, string ReferenceId);

public sealed record NCIncidentOutcome(string OutcomeKey, IReadOnlyList<NCPartyRef> ResponsibleParties,
    IReadOnlyList<Guid> ConsequenceWorldEventIds, int Casualties, long EconomicLoss);

public sealed record NCCityIncidentSnapshot(
    NCCityIncidentId IncidentId,
    NCCityIncidentKind Kind,
    int Severity,
    NCCityIncidentStage Stage,
    NCZoneId LocationZone,
    IReadOnlyList<NCIncidentParticipant> Participants,
    IReadOnlyList<NCIncidentCause> Causes,
    IReadOnlyList<NCIncidentEvidence> Evidence,
    IReadOnlyList<NCIncidentWitnessStatement> WitnessStatements,
    IReadOnlyList<Guid> DispatchIds,
    NCIncidentMaterializationPolicy MaterializationPolicy,
    NCActiveWorldTime? EscalatesAt,
    NCIncidentOutcome? Outcome,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

[Serializable, NetSerializable]
public enum NCCityServiceKind : byte
{
    Ncpd,
    TraumaTeam,
    MaxTac,
    Medical,
    Gang,
    Corporate,
}

[Serializable, NetSerializable]
public enum NCServiceDispatchStatus : byte
{
    Queued,
    Assigned,
    EnRoute,
    OnScene,
    Completed,
    Rejected,
    Cancelled,
}

[Serializable, NetSerializable]
public readonly record struct NCServiceDispatchId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCServiceContractId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

public sealed record NCServiceCapacity(int TotalTeams, int AvailableTeams, int MaximumQueue,
    int MaximumThreatBasisPoints);

public sealed record NCCityServiceSnapshot(NCCityServiceId ServiceId, string StableKey, string DisplayName,
    NCCityServiceKind Kind, NCPartyRef ServiceParty, NCOrganizationId? FactionId, NCZoneId BaseZone,
    NCServiceCapacity Capacity, IReadOnlyList<NCServiceDispatchId> Queue,
    IReadOnlyList<NCServiceDispatchId> ActiveDispatches, int DataVersion, Guid LastAppliedEventId);

public sealed record NCServiceContractSnapshot(NCServiceContractId ContractId, NCCityServiceId ServiceId,
    NCPartyRef Subscriber, int CoverageTier, long MaximumCoveredCost, int MaximumThreatBasisPoints,
    NCActiveWorldTime StartsAt, NCActiveWorldTime? EndsAt, bool Active, int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCServiceDispatchSnapshot(NCServiceDispatchId DispatchId, NCCityServiceId ServiceId,
    NCCityIncidentId IncidentId, NCServiceContractId? ContractId, NCPartyRef Requester,
    NCServiceDispatchStatus Status, NCZoneId OriginZone, NCZoneId DestinationZone, NCCityTravelMode TravelMode,
    int ThreatBasisPoints, IReadOnlyList<NCZoneId> Route, NCActiveWorldTime RequestedAt,
    NCActiveWorldTime? ExpectedArrivalAt, Guid? ResponseGroupId, string DecisionReason,
    int DataVersion, Guid LastAppliedEventId);

public sealed record NCDistrictSafetyProjection(NCZoneId DistrictId, int Threat, int LawPressure,
    int ServiceLoad, int ActiveIncidents, NCPartyRef ControllingFaction, long Revision);
