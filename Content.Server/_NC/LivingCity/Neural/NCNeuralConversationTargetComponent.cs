// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Social;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Data-only hearing and dialogue limits for a physical neural actor.
/// </summary>
[RegisterComponent]
public sealed partial class NCNeuralConversationTargetComponent : Component
{
    /// <summary>True for an authored persona that receives the larger narrative context budget.</summary>
    [DataField]
    public bool NarrativeActor;

    [DataField]
    public float InteractionRange = 2.5f;

    /// <summary>The NPC may hear an unobstructed local speaker without a special interaction verb.</summary>
    [DataField]
    public bool ListenToLocalSpeech = true;

    /// <summary>Allows this resident's ordinary speech to begin a bounded actor-to-actor conversation.</summary>
    [DataField]
    public bool SpeakWithNpcListeners = true;

    /// <summary>Prevents NPC-to-NPC replies from turning into an unbounded neural conversation cascade.</summary>
    [DataField]
    public TimeSpan NpcConversationCooldown = TimeSpan.FromSeconds(30);

    [DataField]
    public int MaximumNpcConversationTurns = 6;

    [DataField]
    public float ListeningRange = 4f;

    [DataField]
    public int MaximumUtteranceLength = 512;

    [DataField]
    public List<string> TopicKeys = [];

    [DataField]
    public NCMemoryPrivacy MaximumDisclosure = NCMemoryPrivacy.Public;

    [DataField]
    public NCLanguage Language = NCLanguage.Russian;

    [DataField]
    public NCLocale Locale = NCLocale.RuRu;

}
