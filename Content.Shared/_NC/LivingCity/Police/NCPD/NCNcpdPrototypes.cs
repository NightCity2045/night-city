// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Police.NCPD;

/// <summary>
/// NPC permissions and grouping limits for one existing player-facing NCPD job.
/// </summary>
[Prototype("ncNcpdRoleProfile")]
public sealed partial class NCNcpdRoleProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public ProtoId<JobPrototype> Job { get; private set; }
    [DataField] public NCNcpdOfficerRole OfficerRole { get; private set; }
    [DataField] public bool CanLead { get; private set; }
    [DataField] public bool MayWorkAlone { get; private set; }
    [DataField] public bool RequiresSupervisor { get; private set; }
    [DataField] public int MinimumGroupSize { get; private set; } = 2;
    [DataField] public int MaximumGroupSize { get; private set; } = 4;
    [DataField] public int MaximumIncidentSeverity { get; private set; } = 1000;
    [DataField] public List<NCNcpdAssignmentKind> AllowedAssignments { get; private set; } = [];
    [DataField] public List<NCCityTravelMode> TravelModes { get; private set; } = [];
    [DataField] public bool CanDetain { get; private set; }
    [DataField] public bool CanInvestigate { get; private set; }
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId WorkZone { get; private set; }
    [DataField(required: true)] public ProtoId<NCNcpdUseOfForcePolicyPrototype> ForcePolicy { get; private set; }
}

/// <summary>
/// Operational authority granted by the character's persistent NCPD job. Organizational rank remains canonical in
/// DepartmentPrototype.NCPositionRanks and is intentionally not duplicated here.
/// </summary>
[Prototype("ncNcpdCommandAuthority")]
public sealed partial class NCNcpdCommandAuthorityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public ProtoId<JobPrototype> Job { get; private set; }
    [DataField] public List<NCNcpdCommandKind> AllowedCommands { get; private set; } = [];
    [DataField] public List<NCZoneContextSlot> AllowedZoneSlots { get; private set; } = [];
    [DataField] public int MaximumPriority { get; private set; } = 1000;
    [DataField] public bool CanCommandAllUnits { get; private set; }
    [DataField] public bool CanCommandEqualRank { get; private set; }
    [DataField] public bool CanCancelOthers { get; private set; }
}

[Prototype("ncNcpdCommandPolicy")]
public sealed partial class NCNcpdCommandPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int MaximumActiveCommands { get; private set; } = 64;
    [DataField] public int RetainedHistory { get; private set; } = 100;
    [DataField] public int MaximumVisibleZones { get; private set; } = 512;
    [DataField] public uint MaximumReasonLength { get; private set; } = 512;
}

[Prototype("ncNcpdDutyPolicy")]
public sealed partial class NCNcpdDutyPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId StationZone { get; private set; }
    [DataField] public int PreferredPatrolSize { get; private set; } = 2;
    [DataField] public int MaximumPatrolSize { get; private set; } = 4;
    [DataField] public int ReserveActors { get; private set; } = 2;
    [DataField] public int MaximumTransitionsPerEvent { get; private set; } = 32;
}

[Prototype("ncNcpdPatrolPolicy")]
public sealed partial class NCNcpdPatrolPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int MaximumStreetZones { get; private set; } = 8;
    [DataField] public TimeSpan MinimumStreetDwell { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan MaximumStreetDwell { get; private set; } = TimeSpan.FromMinutes(6);
    [DataField] public int MaximumReplans { get; private set; } = 3;
    [DataField] public TimeSpan ReplanBackoff { get; private set; } = TimeSpan.FromSeconds(5);
    [DataField] public float FollowSpacing { get; private set; } = 1.5f;
}

[Prototype("ncNcpdDispatchPolicy")]
public sealed partial class NCNcpdDispatchPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int MaximumAssignmentsPerEvaluation { get; private set; } = 8;
    [DataField] public int CadetMaximumSeverity { get; private set; } = 300;
    [DataField] public int InspectorMinimumSeverity { get; private set; } = 500;
    [DataField] public TimeSpan AssignmentTimeout { get; private set; } = TimeSpan.FromMinutes(2);
}

[Prototype("ncNcpdUseOfForcePolicy")]
public sealed partial class NCNcpdUseOfForcePolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int VerbalOrderThreat { get; private set; } = 50;
    [DataField] public int RestraintThreat { get; private set; } = 150;
    [DataField] public int NonLethalThreat { get; private set; } = 350;
    [DataField] public int LethalThreat { get; private set; } = 800;
    [DataField] public bool StopForceOnSurrender { get; private set; } = true;
    [DataField] public bool StopForceOnIncapacitation { get; private set; } = true;
    [DataField] public float IncidentObservationRadius { get; private set; } = 8f;
    [DataField] public TimeSpan SuspectObservationLifetime { get; private set; } = TimeSpan.FromSeconds(45);
    [DataField] public float VerbalOrderRange { get; private set; } = 6f;
    [DataField] public float RestraintRange { get; private set; } = 1.25f;
    [DataField] public TimeSpan RestraintRetryDelay { get; private set; } = TimeSpan.FromSeconds(4);
    [DataField] public string VerbalOrderLocKey { get; private set; } = "nc-ncpd-ai-lawful-order";
}

[Prototype("ncNcpdDetentionPolicy")]
public sealed partial class NCNcpdDetentionPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public bool RequirePoliceBasis { get; private set; } = true;
    [DataField] public bool RequireRestraintsForEscort { get; private set; } = true;
    [DataField] public bool ConfiscateDangerousWeapons { get; private set; } = true;
    [DataField] public TimeSpan BookingTimeout { get; private set; } = TimeSpan.FromMinutes(5);
}

[Prototype("ncNcpdInvestigationPolicy")]
public sealed partial class NCNcpdInvestigationPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int MaximumEvidencePerIncident { get; private set; } = 128;
    [DataField] public int MaximumWitnessesPerIncident { get; private set; } = 64;
    [DataField] public TimeSpan UnresolvedCaseRetention { get; private set; } = TimeSpan.FromHours(24);
}
