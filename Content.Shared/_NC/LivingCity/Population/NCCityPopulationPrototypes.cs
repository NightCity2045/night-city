// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Population;

/// <summary>
/// Cultural naming data used only when a persistent actor is first created.
/// Existing identities never change merely because a pool is rebalanced later.
/// </summary>
[Prototype("ncCityIdentityPool")]
public sealed partial class NCCityIdentityPoolPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<string> GivenNames { get; private set; } = [];

    [DataField(required: true)]
    public List<string> FamilyNames { get; private set; } = [];
}

/// <summary>
/// Declarative persistent population import. ScopeId and stable local slots are the identity authority;
/// changing Count only appends or omits deterministic slots and never renumbers an existing actor.
/// </summary>
[Prototype("ncCityPopulationProfile")]
public sealed partial class NCCityPopulationProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public Guid ScopeId { get; private set; }

    [DataField]
    public List<NCCityPopulationCohort> Cohorts { get; private set; } = [];

    [DataField]
    public List<NCCityPopulationActorEntry> Actors { get; private set; } = [];
}

/// <summary>
/// Generated actor slots sharing one archetype, identity pool and semantic home/work pair.
/// </summary>
[DataDefinition]
public sealed partial class NCCityPopulationCohort
{
    [DataField(required: true)]
    public string Id { get; private set; } = string.Empty;

    [DataField(required: true)]
    public int Count { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCityIdentityPoolPrototype> IdentityPool { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCityActorArchetypePrototype> Archetype { get; private set; }

    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId HomeZone { get; private set; }

    [DataField(customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId WorkZone { get; private set; }

    /// <summary>
    /// Optional formal employment. It references the same JobPrototype available to players; the owning department
    /// is resolved from DepartmentPrototype.Roles during import.
    /// </summary>
    [DataField]
    public ProtoId<JobPrototype>? Job { get; private set; }

    /// <summary>
    /// Optional civilian occupation for residents outside formal organizations. It must not be combined with Job.
    /// </summary>
    [DataField]
    public ProtoId<NCLivelihoodProfilePrototype>? Livelihood { get; private set; }

    [DataField]
    public ProtoId<NCFactionProfilePrototype>? Faction { get; private set; }

    [DataField]
    public string FactionRole { get; private set; } = "member";
}

/// <summary>
/// Explicit named actor entry for authored residents. It uses the same import and idempotency path as cohorts.
/// </summary>
[DataDefinition]
public sealed partial class NCCityPopulationActorEntry
{
    [DataField(required: true)]
    public string StableLocalId { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string FullName { get; private set; } = string.Empty;

    [DataField]
    public List<string> Aliases { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCCityActorArchetypePrototype> Archetype { get; private set; }

    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId HomeZone { get; private set; }

    [DataField(customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId WorkZone { get; private set; }

    [DataField]
    public ProtoId<JobPrototype>? Job { get; private set; }

    [DataField]
    public ProtoId<NCLivelihoodProfilePrototype>? Livelihood { get; private set; }

    [DataField]
    public ProtoId<NCFactionProfilePrototype>? Faction { get; private set; }

    [DataField]
    public string FactionRole { get; private set; } = "member";

    /// <summary>The rare authored profile that turns this resident into a narrative Actor.</summary>
    [DataField]
    public ProtoId<NCNarrativeActorProfilePrototype>? NarrativeActorProfile { get; private set; }

    /// <summary>
    /// Optional deterministic cohort slot replaced by this named person. This preserves population size and lets an
    /// existing development database promote the same persistent actor instead of creating a duplicate.
    /// </summary>
    [DataField]
    public string ReplacesCohortId { get; private set; } = string.Empty;

    [DataField]
    public int ReplacesCohortIndex { get; private set; } = -1;
}

/// <summary>
/// Operational bounds for explicit population imports. These are server-load controls, not simulation rules.
/// </summary>
[Prototype("ncCityPopulationImportPolicy")]
public sealed partial class NCCityPopulationImportPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int MaximumActorsPerProfile { get; private set; } = 10000;

    [DataField]
    public int MaximumConcurrentCreates { get; private set; } = 8;

    [DataField]
    public int StorageRetryCount { get; private set; } = 3;

    [DataField]
    public TimeSpan StorageRetryDelay { get; private set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Controls bounded migration of legacy physical residences into coordinate-free homes.
/// </summary>
[Prototype("ncCityResidenceAllocationPolicy")]
public sealed partial class NCCityResidenceAllocationPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int SnapshotPageSize { get; private set; } = 250;

    [DataField]
    public int MaximumActorsPerRun { get; private set; } = 20000;

    [DataField]
    public int MaximumConcurrentMutations { get; private set; } = 8;

    [DataField(required: true)]
    public List<ProtoId<NCAbstractResidenceProfilePrototype>> ResidenceProfiles { get; private set; } = [];

    [DataField]
    public int StorageRetryCount { get; private set; } = 3;

    [DataField]
    public TimeSpan StorageRetryDelay { get; private set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Bounded active-round projection policy. Activity weights are gameplay data; the projection system only applies
/// the configured table and never embeds a hidden simulation formula.
/// </summary>
[Prototype("ncCityZoneStatePolicy")]
public sealed partial class NCCityZoneStatePolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int SnapshotPageSize { get; private set; } = 250;

    [DataField]
    public int MaximumWritesPerTick { get; private set; } = 2;

    [DataField]
    public int MaximumConcurrentWrites { get; private set; } = 4;

    [DataField]
    public TimeSpan CoalesceDelay { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public TimeSpan StorageRetryDelay { get; private set; } = TimeSpan.FromSeconds(5);

    [DataField]
    public int MaximumArchetypesPerScope { get; private set; } = 256;

    [DataField]
    public string ArchetypeOverflowBucketId { get; private set; } = "other";

    [DataField]
    public float DistrictActivitySaturation { get; private set; } = 1000f;

    [DataField]
    public float SectorActivitySaturation { get; private set; } = 250f;

    [DataField(required: true)]
    public Dictionary<NCCityActorStatus, float> ActivityStatusWeights { get; private set; } = [];

    [DataField(required: true)]
    public Dictionary<NCCityActorAvailability, float> ActivityAvailabilityWeights { get; private set; } = [];
}
