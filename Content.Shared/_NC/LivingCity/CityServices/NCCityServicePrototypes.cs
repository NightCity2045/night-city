// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.CityServices;

[Prototype("ncIncidentPolicy")]
public sealed partial class NCIncidentPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumParticipants { get; private set; } = 64;
    [DataField] public int MaximumEvidence { get; private set; } = 128;
    [DataField] public int MaximumWitnessStatements { get; private set; } = 64;
    [DataField] public int MaximumDispatches { get; private set; } = 16;
    [DataField] public int MaximumSeverity { get; private set; } = 1000;
    [DataField] public TimeSpan DefaultEscalationDelay { get; private set; } = TimeSpan.FromMinutes(5);
    [DataField] public TimeSpan VerificationDelay { get; private set; } = TimeSpan.FromMinutes(1);
    [DataField] public TimeSpan DispatchRetryDelay { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan AbstractResolutionDelay { get; private set; } = TimeSpan.FromMinutes(10);
    [DataField] public TimeSpan ClosureDelay { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public int MaxTacSeverityThreshold { get; private set; } = 800;
    [DataField] public int MaxTacCyberpsychosisThreshold { get; private set; } = 600;
    [DataField] public int MaximumAdvancesPerTick { get; private set; } = 8;
    [DataField] public Dictionary<NCCityIncidentKind, List<ProtoId<NCCityServicePrototype>>> ResponseServices
        { get; private set; } = [];
    [DataField] public ProtoId<NCMemoryEventPrototype> OutcomeMemory { get; private set; } = "MemoryIncidentOutcome";
    [DataField] public int MaximumOutcomeMemoryParticipants { get; private set; } = 16;
    [DataField] public Dictionary<NCCityIncidentKind, ProtoId<NCEconomicServicePrototype>> OutcomeServiceDemands
        { get; private set; } = [];
    [DataField] public TimeSpan OutcomeDemandLifetime { get; private set; } = TimeSpan.FromHours(12);
    [DataField] public long MaximumOutcomeDemandPrice { get; private set; } = 100_000;
    [DataField] public int ResponsibleFactionInfluenceLossPermille { get; private set; } = 100;
}

[Prototype("ncCityServicePolicy")]
public sealed partial class NCCityServicePolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public NCCityServiceKind Kind { get; private set; }
    [DataField] public int DefaultTeams { get; private set; } = 4;
    [DataField] public int MaximumQueue { get; private set; } = 64;
    [DataField] public int MaximumThreatBasisPoints { get; private set; } = 10_000;
    [DataField] public NCCityTravelMode TravelMode { get; private set; } = NCCityTravelMode.Vehicle;
    [DataField] public TimeSpan MinimumEta { get; private set; } = TimeSpan.FromMinutes(1);
    [DataField] public TimeSpan MaximumEta { get; private set; } = TimeSpan.FromMinutes(30);
    [DataField] public int SeverityThreshold { get; private set; }
    [DataField] public bool RequiresContract { get; private set; }
}

[Prototype("ncCityService")]
public sealed partial class NCCityServicePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = default!;
    [DataField(required: true)] public string DisplayName { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCityServicePolicyPrototype> Policy { get; private set; }
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId BaseZone { get; private set; }
    [DataField] public NCOrganizationId? FactionId { get; private set; }
}

[Prototype("ncFactionPolicy")]
public sealed partial class NCFactionPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumTerritories { get; private set; } = 256;
    [DataField] public int MaximumRelations { get; private set; } = 128;
    [DataField] public int MaximumInfluence { get; private set; } = 1000;
    [DataField] public int ControlThreshold { get; private set; } = 500;
    [DataField] public int ControlMargin { get; private set; } = 100;
}

[Prototype("ncHumanityPolicy")]
public sealed partial class NCHumanityPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int DefaultMaximumHumanity { get; private set; } = 100;
    [DataField] public int StrainedThreshold { get; private set; } = 50;
    [DataField] public int DissociatingThreshold { get; private set; } = 25;
    [DataField] public int CyberpsychosisThreshold { get; private set; }
    [DataField] public int MaximumAppliedSources { get; private set; } = 512;
}
