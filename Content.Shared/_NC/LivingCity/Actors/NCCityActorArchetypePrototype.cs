// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.Neural;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven materialization and scheduling defaults for one persistent-actor archetype.
/// </summary>
[Prototype("ncCityActorArchetype")]
public sealed partial class NCCityActorArchetypePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public EntProtoId EntityPrototype { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCitySpawnCandidateProfilePrototype> SpawnCandidateProfile { get; private set; }

    [DataField(required: true)]
    public NCCityTravelMode TravelMode { get; private set; }

    [DataField(required: true)]
    public NCCityActorCognitionTier CognitionTier { get; private set; }

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> AllowedHomeContexts { get; private set; } = [];

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> AllowedWorkContexts { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCCityRoutineProfilePrototype> RoutineProfile { get; private set; }

    /// <summary>
    /// Physical traversal capabilities are separate from cognition and the semantic travel mode.
    /// </summary>
    [DataField]
    public ProtoId<NCCityPhysicalNavigationProfilePrototype> PhysicalNavigationProfile { get; private set; } =
        new("NCDefaultPedestrianPhysicalNavigation");

    [DataField]
    public ProtoId<NCCityPhysicalStateTreePrototype> PhysicalStateTree { get; private set; } =
        new("NCDefaultPhysicalStateTree");

    [DataField]
    public ProtoId<NCCityLocalPerceptionProfilePrototype> LocalPerceptionProfile { get; private set; } =
        new("NCDefaultLocalPerception");

    /// <summary>
    /// Persistent BDI policy and the capability allowlist for this archetype. Gameplay weights remain in prototypes.
    /// </summary>
    [DataField]
    public ProtoId<NCCognitionDecisionPolicyPrototype> CognitionDecisionPolicy { get; private set; } =
        new("NCDefaultCognitionDecisionPolicy");

    [DataField]
    public ProtoId<NCCognitionCapabilityProfilePrototype> CognitionCapabilityProfile { get; private set; } =
        new("NCDefaultCivilianCapabilities");

    [DataField]
    public ProtoId<NCCognitionRoutingProfilePrototype> CognitionRoutingProfile { get; private set; } =
        new("NCDefaultCognitionRouting");

    [DataField]
    public ProtoId<NCPersonalityProfilePrototype> PersonalityProfile { get; private set; } =
        new("NCDefaultCivilianPersonality");

    [DataField]
    public ProtoId<NCValuesProfilePrototype> ValuesProfile { get; private set; } =
        new("NCDefaultCivilianValues");

    [DataField]
    public ProtoId<NCSocialPreferenceProfilePrototype> SocialPreferenceProfile { get; private set; } =
        new("NCDefaultCivilianSocialPreferences");

    [DataField]
    public ProtoId<NCSpeechProfilePrototype> SpeechProfile { get; private set; } =
        new("NCDefaultBilingualSpeech");

    /// <summary>
    /// Optional authored profile for an important narrative actor. Ordinary residents never receive Neural access.
    /// </summary>
    [DataField]
    public ProtoId<NCNarrativeActorProfilePrototype>? NarrativeActorProfile { get; private set; }
}
