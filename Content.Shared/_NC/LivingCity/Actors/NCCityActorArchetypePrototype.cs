// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.Neural;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven body, perception and GPT defaults for one persistent physical actor archetype.
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

    [DataField]
    public ProtoId<NCCityLocalPerceptionProfilePrototype> LocalPerceptionProfile { get; private set; } =
        new("NCDefaultLocalPerception");

    /// <summary>
    /// Typed command allowlist available to the GPT brain. Gameplay authority remains in ECS systems.
    /// </summary>
    [DataField]
    public ProtoId<NCCognitionCapabilityProfilePrototype> CognitionCapabilityProfile { get; private set; } =
        new("NCDefaultCivilianCapabilities");

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
    /// Optional richer profile for a future authored actor. Ordinary residents still use GPT through their persona.
    /// </summary>
    [DataField]
    public ProtoId<NCNarrativeActorProfilePrototype>? NarrativeActorProfile { get; private set; }
}
