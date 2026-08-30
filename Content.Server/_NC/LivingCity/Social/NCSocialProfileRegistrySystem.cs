// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

public sealed record NCSocialProfileRecord(
    NCSocialProfileSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

/// <summary>
/// Persistent, immutable social profile authority for non-player actors. It copies authored profile values at actor
/// creation, so a later YAML rebalance does not silently rewrite an existing person's temperament or languages.
/// </summary>
public sealed partial class NCSocialProfileRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.social-profile";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumStableKeyLength = 96;
    private const int MaximumForbiddenTopics = 32;
    private const int MaximumUtilityModifiers = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly ConcurrentDictionary<NCActorId, NCSocialProfileRecord> _cache = [];

    public int CachedProfileCount => _cache.Count;

    public bool Unload(NCActorId actorId) => _cache.TryRemove(actorId, out _);

    public async Task<NCSocialProfileRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
        var record = Deserialize(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    public NCPersistentAggregateWrite BuildInitialWrite(
        NCActorId actorId,
        NCCityActorArchetypePrototype archetype,
        NCActiveWorldTime now,
        string auditSource)
    {
        if (!actorId.IsValid || !now.IsValid || string.IsNullOrWhiteSpace(auditSource) ||
            !_prototypes.TryIndex(archetype.PersonalityProfile, out NCPersonalityProfilePrototype? personality) ||
            !_prototypes.TryIndex(archetype.ValuesProfile, out NCValuesProfilePrototype? values) ||
            !_prototypes.TryIndex(archetype.SocialPreferenceProfile,
                out NCSocialPreferenceProfilePrototype? preferences) ||
            !_prototypes.TryIndex(archetype.SpeechProfile, out NCSpeechProfilePrototype? speech) ||
            !Valid(personality) || !Valid(values) || !Valid(preferences) || !Valid(speech))
        {
            throw new InvalidOperationException($"Actor archetype {archetype.ID} has an invalid social profile set.");
        }

        var eventId = Guid.NewGuid();
        var snapshot = new NCSocialProfileSnapshot(
            actorId,
            personality.ID,
            values.ID,
            preferences.ID,
            speech.ID,
            new NCPersonalityTraits(
                personality.Temperament.Trim().ToLowerInvariant(),
                personality.RiskTolerance,
                personality.Aggression,
                personality.Empathy,
                personality.Greed,
                personality.Loyalty,
                personality.Secrecy,
                personality.Patience,
                personality.Pride,
                personality.AuthorityRespect,
                personality.ViolenceTolerance,
                personality.CivilianHarmTolerance),
            new NCValuePriorities(
                values.Family,
                values.Money,
                values.Reputation,
                values.Faction,
                values.Survival,
                values.Freedom,
                values.Justice,
                values.Professionalism,
                values.Revenge),
            new NCSpeechPreferences(
                speech.Register.Trim().ToLowerInvariant(),
                speech.Vocabulary.Trim().ToLowerInvariant(),
                speech.SentenceLength,
                speech.Directness,
                speech.SlangLevel,
                speech.Humor,
                speech.Politeness,
                speech.PreferredLanguage,
                speech.PreferredLocale,
                speech.Languages.Order().ToArray(),
                speech.ForbiddenTopics.Select(NormalizeStableKey).Order(StringComparer.Ordinal).ToArray()),
            preferences.CapabilityUtilityModifiers
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key.Trim(), value => value.Value, StringComparer.Ordinal),
            now,
            1,
            eventId);
        return new NCPersistentAggregateWrite(
            eventId,
            "living-city.social-profile.created",
            AggregateType,
            ActorKey(actorId),
            0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new SocialProfileCreatedEvent(actorId, auditSource.Trim(), snapshot),
                JsonOptions));
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var value in _prototypes.EnumeratePrototypes<NCPersonalityProfilePrototype>())
        {
            if (!Valid(value))
                errors.Add($"Personality profile {value.ID} is invalid.");
        }
        foreach (var value in _prototypes.EnumeratePrototypes<NCValuesProfilePrototype>())
        {
            if (!Valid(value))
                errors.Add($"Values profile {value.ID} is invalid.");
        }
        foreach (var value in _prototypes.EnumeratePrototypes<NCSocialPreferenceProfilePrototype>())
        {
            if (!Valid(value))
                errors.Add($"Social preference profile {value.ID} is invalid.");
        }
        foreach (var value in _prototypes.EnumeratePrototypes<NCSpeechProfilePrototype>())
        {
            if (!Valid(value))
                errors.Add($"Speech profile {value.ID} is invalid.");
        }
        return errors.Count == initial;
    }

    private static NCSocialProfileRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion != 1 ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCSocialProfileSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.CreatedAt == stored.ActiveWorldTime
                ? new NCSocialProfileRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCSocialProfileRecord CacheNewest(NCSocialProfileRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.ActorId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private bool Valid(NCSocialPreferenceProfilePrototype value) =>
        IsStableKey(value.StableKey) && value.CapabilityUtilityModifiers.Count <= MaximumUtilityModifiers &&
        value.CapabilityUtilityModifiers.All(pair => IsPrototypeKey(pair.Key) && float.IsFinite(pair.Value) &&
            pair.Value is >= -1f and <= 1f && _prototypes.TryIndex(
                new ProtoId<NCCognitionCapabilityPrototype>(pair.Key), out _));

    private static bool Valid(NCPersonalityProfilePrototype value) =>
        IsStableKey(value.StableKey) && IsStableKey(value.Temperament) &&
        AllScores([
            value.RiskTolerance, value.Aggression, value.Empathy, value.Greed, value.Loyalty, value.Secrecy,
            value.Patience, value.Pride, value.AuthorityRespect, value.ViolenceTolerance,
            value.CivilianHarmTolerance,
        ]);

    private static bool Valid(NCValuesProfilePrototype value) => IsStableKey(value.StableKey) &&
        AllScores([
            value.Family, value.Money, value.Reputation, value.Faction, value.Survival, value.Freedom,
            value.Justice, value.Professionalism, value.Revenge,
        ]);

    private static bool Valid(NCSpeechProfilePrototype value) =>
        IsStableKey(value.StableKey) && IsStableKey(value.Register) && IsStableKey(value.Vocabulary) &&
        AllScores([value.SentenceLength, value.Directness, value.SlangLevel, value.Humor, value.Politeness]) &&
        value.Languages.Count is > 0 and <= 2 && value.Languages.All(Enum.IsDefined) &&
        value.Languages.Contains(value.PreferredLanguage) && Enum.IsDefined(value.PreferredLocale) &&
        value.ForbiddenTopics.Count <= MaximumForbiddenTopics && value.ForbiddenTopics.All(IsStableKey);

    private static bool Valid(NCSocialProfileSnapshot? value, NCActorId expected) => value != null &&
        value.ActorId == expected && expected.IsValid && IsPrototypeKey(value.PersonalityProfileId) &&
        IsPrototypeKey(value.ValuesProfileId) && IsPrototypeKey(value.PreferenceProfileId) &&
        IsPrototypeKey(value.SpeechProfileId) && IsStableKey(value.Personality.Temperament) &&
        AllScores([
            value.Personality.RiskTolerance, value.Personality.Aggression, value.Personality.Empathy,
            value.Personality.Greed, value.Personality.Loyalty, value.Personality.Secrecy,
            value.Personality.Patience, value.Personality.Pride, value.Personality.AuthorityRespect,
            value.Personality.ViolenceTolerance, value.Personality.CivilianHarmTolerance,
            value.Values.Family, value.Values.Money, value.Values.Reputation, value.Values.Faction,
            value.Values.Survival, value.Values.Freedom, value.Values.Justice, value.Values.Professionalism,
            value.Values.Revenge, value.Speech.SentenceLength, value.Speech.Directness, value.Speech.SlangLevel,
            value.Speech.Humor, value.Speech.Politeness,
        ]) && IsStableKey(value.Speech.Register) && IsStableKey(value.Speech.Vocabulary) &&
        value.Speech.Languages.Count is > 0 and <= 2 && value.Speech.Languages.Distinct().Count() ==
        value.Speech.Languages.Count && value.Speech.Languages.Contains(value.Speech.PreferredLanguage) &&
        value.Speech.ForbiddenTopics.Count <= MaximumForbiddenTopics && value.Speech.ForbiddenTopics.All(IsStableKey) &&
        value.CapabilityUtilityModifiers.Count <= MaximumUtilityModifiers &&
        value.CapabilityUtilityModifiers.All(pair => IsPrototypeKey(pair.Key) && float.IsFinite(pair.Value) &&
            pair.Value is >= -1f and <= 1f) && value.CreatedAt.IsValid && value.DataVersion == 1 &&
        value.LastAppliedEventId != Guid.Empty;

    private static bool AllScores(IEnumerable<int> values) => values.All(value => value is >= 0 and <= 1_000);

    private static bool IsPrototypeKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumStableKeyLength && value == value.Trim();

    private static bool IsStableKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumStableKeyLength && value == NormalizeStableKey(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string NormalizeStableKey(string value) => value.Trim().ToLowerInvariant();
    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private sealed record SocialProfileCreatedEvent(
        NCActorId ActorId,
        string AuditSource,
        NCSocialProfileSnapshot Snapshot);
}
