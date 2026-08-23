// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Social;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Data-only marker for a physical actor that may enter an explicit, player-initiated neural conversation.
/// </summary>
[RegisterComponent]
public sealed partial class NCNeuralConversationTargetComponent : Component
{
    [DataField]
    public float InteractionRange = 2.5f;

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
