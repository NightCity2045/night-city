// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Employment;

/// <summary>
/// Adds profession-specific cognition to the common human mind. Jobs remain the same prototypes used by players;
/// this profile only declares NPC knowledge and capability overlays and never creates a parallel job hierarchy.
/// </summary>
[Prototype("ncProfessionProfile")]
public sealed partial class NCProfessionProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<DepartmentPrototype> Organization { get; private set; }
    [DataField(required: true)] public HashSet<ProtoId<JobPrototype>> Jobs { get; private set; } = [];
    [DataField] public ProtoId<NCCognitionCapabilityPrototype>[] CapabilityOverlays { get; private set; } = [];
    [DataField] public string[] SkillKeys { get; private set; } = [];
    /// <summary>Semantic destination used by the generic physical duty adapter; it is not a mapper marker.</summary>
    [DataField] public NCCityAffordanceKind WorkAffordance { get; private set; } = NCCityAffordanceKind.Workplace;
}

[Prototype("ncGenericProfessionalWorkPolicy")]
public sealed partial class NCGenericProfessionalWorkPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype> Capability { get; private set; }
    [DataField] public float ArrivalRange { get; private set; } = 1.25f;
    [DataField] public int MaximumCatalogCandidates { get; private set; } = 32;
}

/// <summary>
/// Bounded physical-care policy for City Medical clinical staff. Treatment priorities and search limits live in
/// content data so the cognition adapter only selects and executes existing authoritative medical interactions.
/// </summary>
[Prototype("ncCityMedicalWorkPolicy")]
public sealed partial class NCCityMedicalWorkPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<DepartmentPrototype> Organization { get; private set; }
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype> Capability { get; private set; }
    [DataField(required: true)] public NCTreatmentKind[] TreatmentPriority { get; private set; } = [];
    [DataField] public int MaximumInventoryCandidates { get; private set; } = 32;
    [DataField] public int MaximumInterventionsPerPatient { get; private set; } = 3;
    [DataField] public TimeSpan CompletionGrace { get; private set; } = TimeSpan.FromSeconds(2);
}
