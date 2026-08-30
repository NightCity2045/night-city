// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Social;

[Serializable, NetSerializable]
public enum NCLanguage : byte
{
    Russian,
    English,
}

[Serializable, NetSerializable]
public enum NCLocale : byte
{
    RuRu,
    EnUs,
}

public sealed record NCPersonalityTraits(
    string Temperament,
    int RiskTolerance,
    int Aggression,
    int Empathy,
    int Greed,
    int Loyalty,
    int Secrecy,
    int Patience,
    int Pride,
    int AuthorityRespect,
    int ViolenceTolerance,
    int CivilianHarmTolerance);

public sealed record NCValuePriorities(
    int Family,
    int Money,
    int Reputation,
    int Faction,
    int Survival,
    int Freedom,
    int Justice,
    int Professionalism,
    int Revenge);

public sealed record NCSpeechPreferences(
    string Register,
    string Vocabulary,
    int SentenceLength,
    int Directness,
    int SlangLevel,
    int Humor,
    int Politeness,
    NCLanguage PreferredLanguage,
    NCLocale PreferredLocale,
    IReadOnlyList<NCLanguage> Languages,
    IReadOnlyList<string> ForbiddenTopics);

/// <summary>
/// Immutable personality selected when an actor is created. Utility modifiers are bounded additive inputs and never
/// grant a capability that the actor's cognition profile does not already allow.
/// </summary>
public sealed record NCSocialProfileSnapshot(
    NCActorId ActorId,
    string PersonalityProfileId,
    string ValuesProfileId,
    string PreferenceProfileId,
    string SpeechProfileId,
    NCPersonalityTraits Personality,
    NCValuePriorities Values,
    NCSpeechPreferences Speech,
    IReadOnlyDictionary<string, float> CapabilityUtilityModifiers,
    NCActiveWorldTime CreatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

