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
    [DataField]
    public float InteractionRange = 2.5f;

    /// <summary>The NPC may acquire the nearest unobstructed local speaker without a special interaction verb.</summary>
    [DataField]
    public bool ListenToLocalSpeech = true;

    /// <summary>Allows this resident's ordinary speech to begin a bounded actor-to-actor conversation.</summary>
    [DataField]
    public bool SpeakWithNpcListeners = true;

    /// <summary>Prevents ambient replies from turning into an unbounded neural conversation cascade.</summary>
    [DataField]
    public TimeSpan NpcConversationCooldown = TimeSpan.FromSeconds(30);

    [DataField]
    public int MaximumNpcConversationTurns = 6;

    /// <summary>Allows rare round-local conversations to start without player input.</summary>
    [DataField]
    public bool InitiateAmbientConversations = true;

    [DataField]
    public TimeSpan MinimumAmbientConversationInterval = TimeSpan.FromMinutes(8);

    [DataField]
    public TimeSpan MaximumAmbientConversationInterval = TimeSpan.FromMinutes(15);

    [DataField]
    public int MaximumAmbientCandidates = 12;

    [DataField]
    public List<string> AmbientOpeners =
    [
        "Слышал, что сегодня происходит?",
        "Как у тебя смена проходит?",
        "Есть что нового в районе?",
        "Ты как, держишься?",
    ];

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

    [DataField]
    public string FallbackMessage = "nc-neural-conversation-fallback";
}
