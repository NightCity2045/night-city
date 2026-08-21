// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

[Prototype("ncPersonalityProfile")]
public sealed partial class NCPersonalityProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string Temperament { get; private set; } = string.Empty;
    [DataField] public int RiskTolerance { get; private set; } = 500;
    [DataField] public int Aggression { get; private set; } = 500;
    [DataField] public int Empathy { get; private set; } = 500;
    [DataField] public int Greed { get; private set; } = 500;
    [DataField] public int Loyalty { get; private set; } = 500;
    [DataField] public int Secrecy { get; private set; } = 500;
    [DataField] public int Patience { get; private set; } = 500;
    [DataField] public int Pride { get; private set; } = 500;
    [DataField] public int AuthorityRespect { get; private set; } = 500;
    [DataField] public int ViolenceTolerance { get; private set; } = 500;
    [DataField] public int CivilianHarmTolerance { get; private set; } = 100;
}

[Prototype("ncValuesProfile")]
public sealed partial class NCValuesProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField] public int Family { get; private set; } = 500;
    [DataField] public int Money { get; private set; } = 500;
    [DataField] public int Reputation { get; private set; } = 500;
    [DataField] public int Faction { get; private set; } = 500;
    [DataField] public int Survival { get; private set; } = 800;
    [DataField] public int Freedom { get; private set; } = 500;
    [DataField] public int Justice { get; private set; } = 500;
    [DataField] public int Professionalism { get; private set; } = 500;
    [DataField] public int Revenge { get; private set; } = 500;
}

[Prototype("ncSocialPreferenceProfile")]
public sealed partial class NCSocialPreferenceProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;

    /// <summary>
    /// Additive Utility adjustments keyed by an existing cognition capability prototype ID.
    /// </summary>
    [DataField] public Dictionary<string, float> CapabilityUtilityModifiers { get; private set; } = [];
}

[Prototype("ncSpeechProfile")]
public sealed partial class NCSpeechProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string Register { get; private set; } = string.Empty;
    [DataField(required: true)] public string Vocabulary { get; private set; } = string.Empty;
    [DataField] public int SentenceLength { get; private set; } = 500;
    [DataField] public int Directness { get; private set; } = 500;
    [DataField] public int SlangLevel { get; private set; } = 500;
    [DataField] public int Humor { get; private set; } = 500;
    [DataField] public int Politeness { get; private set; } = 500;
    [DataField] public NCLanguage PreferredLanguage { get; private set; } = NCLanguage.Russian;
    [DataField] public NCLocale PreferredLocale { get; private set; } = NCLocale.RuRu;
    [DataField(required: true)] public HashSet<NCLanguage> Languages { get; private set; } = [];
    [DataField] public HashSet<string> ForbiddenTopics { get; private set; } = [];
}

