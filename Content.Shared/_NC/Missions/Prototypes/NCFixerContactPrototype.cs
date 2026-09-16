// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Prototypes;

/// <summary>One authored customer contact; budgets and access thresholds are content, not character levels.</summary>
[Prototype("ncFixerContact")]
public sealed partial class NCFixerContactPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public Guid ActorId;
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public ProtoId<NCCityActorArchetypePrototype> Archetype;
    [DataField(required: true)] public ProtoId<NCNeuralPersonaProfilePrototype> Persona;
    [DataField(required: true)] public Dictionary<ProtoId<NCMissionDefinitionPrototype>, int> Missions = [];
    [DataField(required: true)] public long OpeningBudget;
    [DataField(required: true)] public int OrganizerTrustThreshold;
    [DataField(required: true)] public TimeSpan OfferCooldown;
    [DataField(required: true)] public TimeSpan DeliveryWindow;
    [DataField(required: true)] public TimeSpan PresenceCheckInterval;
    [DataField(required: true)] public ProtoId<NCReputationEffectPrototype> SuccessEffect;
    [DataField(required: true)] public ProtoId<NCReputationDomainPrototype> ExecutorDomain;
    [DataField(required: true)] public ProtoId<NCReputationDomainPrototype> OrganizerDomain;
}
