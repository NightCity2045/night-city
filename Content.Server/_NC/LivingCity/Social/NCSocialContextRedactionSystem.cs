// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>
/// The sole social-context projection intended for neural consumers. Domain registries keep full authority; this
/// projection strips provenance IDs and enforces both purpose and ConversationSession disclosure ceilings.
/// </summary>
public sealed partial class NCSocialContextRedactionSystem : EntitySystem
{
    private static readonly ProtoId<NCSocialContextPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private NCRumorRegistrySystem _rumors = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCSocialProfileRegistrySystem _profiles = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;

    public async Task<NCRedactedSocialContext?> BuildAsync(
        NCPartyRef owner,
        NCSocialContextPurpose purpose,
        NCConversationSessionId? conversationSessionId = null,
        IReadOnlyDictionary<NCPartyRef, string>? participantAliases = null)
    {
        if (!owner.IsValid || !Enum.IsDefined(purpose) || !TryGetPolicy(out var policy))
            return null;

        if (!TryResolveMaximumPrivacy(owner, purpose, conversationSessionId, out var maximum))
            return null;

        var memoryBookTask = _memories.GetAsync(owner);
        var memoriesTask = _memories.GetRelevantAsync(owner,
            new NCMemoryQuery([], [], [], maximum, policy.MaximumItemsPerDomain));
        var rumorBookTask = _rumors.GetAsync(owner);
        var rumorsTask = _rumors.GetKnownAsync(owner, maximum, policy.MaximumItemsPerDomain);
        var obligationsTask = _obligations.GetForPartyAsync(owner, policy.MaximumItemsPerDomain, false);
        var relationshipsTask = _relationships.GetForObserverAsync(owner, policy.MaximumItemsPerDomain);
        NCSocialProfileRecord? profile = null;
        if (owner.TryGetActor(out var actorId))
            profile = await _profiles.GetAsync(actorId);
        await Task.WhenAll(memoryBookTask, memoriesTask, rumorBookTask, rumorsTask, obligationsTask,
            relationshipsTask);

        var facts = new List<NCRedactedSocialFact>(policy.MaximumItemsPerDomain * 4);
        var opaqueParties = new Dictionary<NCPartyRef, string>();
        var memoryFacts = 0;
        foreach (var item in await memoriesTask)
        {
            if (memoryFacts >= policy.MaximumItemsPerDomain)
                break;
            if (item.Episode is { } episode)
            {
                // Directly witnessed violence stores actor first and victim second. Preserve both roles as
                // structured facts so a neural rendering cannot confuse who performed the act.
                for (var index = 0; index < Math.Min(2, episode.Participants.Count); index++)
                {
                    if (memoryFacts >= policy.MaximumItemsPerDomain)
                        break;
                    facts.Add(new NCRedactedSocialFact(
                        "memory",
                        index == 0 ? episode.EventTypeKey : $"{episode.EventTypeKey}.target",
                        SafePartySubject(episode.Participants[index], participantAliases, opaqueParties),
                        episode.Confidence / 1000f,
                        episode.Privacy,
                        episode.CreatedAt.Ticks));
                    memoryFacts++;
                }
                if (episode.Participants.Count == 0 && memoryFacts < policy.MaximumItemsPerDomain)
                {
                    facts.Add(new NCRedactedSocialFact(
                        "memory",
                        episode.EventTypeKey,
                        "unknown-person",
                        episode.Confidence / 1000f,
                        episode.Privacy,
                        episode.CreatedAt.Ticks));
                    memoryFacts++;
                }
            }
            else if (item.Summary is { } summary)
            {
                facts.Add(new NCRedactedSocialFact(
                    "memory-summary",
                    summary.ConsolidationKey,
                    SafePartySubject(summary.Participants.FirstOrDefault(), participantAliases, opaqueParties),
                    summary.MinimumConfidence / 1000f,
                    summary.Privacy,
                    summary.LastOccurredAt.Ticks));
                memoryFacts++;
            }
        }
        foreach (var rumor in await rumorsTask)
        {
            facts.Add(new NCRedactedSocialFact(
                "rumor",
                rumor.TopicKey,
                SafeInformationSubject(rumor.Subject, participantAliases, opaqueParties),
                rumor.Confidence / 1000f,
                rumor.Privacy,
                rumor.LearnedAt.Ticks));
        }
        var obligations = await obligationsTask;
        foreach (var obligation in obligations.Where(value => value.Snapshot.Privacy <= maximum))
        {
            var snapshot = obligation.Snapshot;
            var counterparty = snapshot.Debtor == owner ? snapshot.Creditor : snapshot.Debtor;
            facts.Add(new NCRedactedSocialFact(
                "obligation",
                snapshot.Terms.PurposeKey,
                SafePartySubject(counterparty, participantAliases, opaqueParties),
                1f,
                snapshot.Privacy,
                snapshot.CreatedAt.Ticks));
        }
        foreach (var relationship in await relationshipsTask)
        {
            var snapshot = relationship.Snapshot;
            var salience = new[]
            {
                Math.Abs(snapshot.Trust),
                Math.Abs(snapshot.Fear),
                Math.Abs(snapshot.Respect),
                Math.Abs(snapshot.Affection),
                Math.Abs(snapshot.Hostility),
                Math.Abs(snapshot.Obligation),
            }.Max();
            facts.Add(new NCRedactedSocialFact(
                "relationship",
                StableKey(snapshot.LastEffectId) ? snapshot.LastEffectId : "known-relationship",
                SafePartySubject(snapshot.Subject, participantAliases, opaqueParties),
                Math.Clamp(salience / 1000f, 0f, 1f),
                NCMemoryPrivacy.Personal,
                snapshot.UpdatedAt.Ticks));
        }

        var memoryBook = await memoryBookTask;
        var rumorBook = await rumorBookTask;
        var allMemories = memoryBook == null
            ? 0
            : memoryBook.Snapshot.Episodes.Count + memoryBook.Snapshot.Summaries.Count;
        var allRumors = rumorBook?.Snapshot.Knowledge.Count ?? 0;
        return new NCRedactedSocialContext(
            owner,
            purpose,
            maximum,
            facts.OrderBy(value => value.Domain, StringComparer.Ordinal)
                .ThenBy(value => value.FactKey, StringComparer.Ordinal).ToArray(),
            (profile?.Snapshot.Speech.ForbiddenTopics ?? []).Take(policy.MaximumForbiddenTopics).ToArray(),
            policy.ForbiddenAssertionKeys.Take(64).ToArray(),
            Math.Max(0, allMemories - (await memoriesTask).Count),
            Math.Max(0, allRumors - (await rumorsTask).Count),
            obligations.Count(value => value.Snapshot.Privacy > maximum));
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var value in _prototypes.EnumeratePrototypes<NCSocialContextPolicyPrototype>())
        {
            if (!Valid(value))
                errors.Add($"Social context policy {value.ID} is invalid.");
        }
        if (!TryGetPolicy(out _))
            errors.Add("The Default social context policy is missing or invalid.");
        return errors.Count == initial;
    }

    /// <summary>Resolves the effective ceiling without loading any social aggregate.</summary>
    public bool TryResolveMaximumPrivacy(
        NCPartyRef owner,
        NCSocialContextPurpose purpose,
        NCConversationSessionId? conversationSessionId,
        out NCMemoryPrivacy maximum)
    {
        maximum = NCMemoryPrivacy.Public;
        if (!owner.IsValid || !Enum.IsDefined(purpose) || !TryGetPolicy(out var policy))
            return false;

        maximum = purpose switch
        {
            NCSocialContextPurpose.InternalReasoning => policy.InternalMaximumPrivacy,
            NCSocialContextPurpose.ConversationRendering => policy.ConversationMaximumPrivacy,
            NCSocialContextPurpose.PublicRendering => policy.PublicMaximumPrivacy,
            _ => NCMemoryPrivacy.Public,
        };
        if (purpose != NCSocialContextPurpose.ConversationRendering)
            return true;
        if (conversationSessionId is not { IsValid: true } sessionId ||
            !_conversations.TryGet(sessionId, out var session) || !session.Participants.Contains(owner))
        {
            return false;
        }

        maximum = (NCMemoryPrivacy) Math.Min((byte) maximum, (byte) session.MaximumDisclosure);
        return true;
    }

    private bool TryGetPolicy(out NCSocialContextPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    private static bool Valid(NCSocialContextPolicyPrototype value) =>
        value.MaximumItemsPerDomain is > 0 and <= 32 && value.MaximumForbiddenTopics is > 0 and <= 128 &&
        value.ForbiddenAssertionKeys.Count <= 64 && value.ForbiddenAssertionKeys.All(StableKey) &&
        Enum.IsDefined(value.InternalMaximumPrivacy) &&
        Enum.IsDefined(value.ConversationMaximumPrivacy) && Enum.IsDefined(value.PublicMaximumPrivacy) &&
        value.ConversationMaximumPrivacy <= value.InternalMaximumPrivacy &&
        value.PublicMaximumPrivacy <= value.ConversationMaximumPrivacy;

    private static bool StableKey(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    /// <summary>
    /// Converts canonical identity to a request-scoped handle. A supplied perception alias preserves the connection
    /// between a remembered participant and the person currently in view; all other identities remain opaque.
    /// </summary>
    private static string SafePartySubject(
        NCPartyRef party,
        IReadOnlyDictionary<NCPartyRef, string>? aliases,
        Dictionary<NCPartyRef, string> opaqueParties)
    {
        if (!party.IsValid)
            return "unknown-person";
        if (aliases != null && aliases.TryGetValue(party, out var alias) && !string.IsNullOrWhiteSpace(alias))
            return alias;
        if (opaqueParties.TryGetValue(party, out var existing))
            return existing;
        var handle = $"known-person-{opaqueParties.Count + 1}";
        opaqueParties.Add(party, handle);
        return handle;
    }

    private static string SafeInformationSubject(
        NCInformationSubject subject,
        IReadOnlyDictionary<NCPartyRef, string>? aliases,
        Dictionary<NCPartyRef, string> opaqueParties)
    {
        if (subject.Kind == NCInformationSubjectKind.Party &&
            NCPartyRef.TryParse(subject.StableId, out var party))
        {
            return SafePartySubject(party, aliases, opaqueParties);
        }

        return StableKey(subject.StableId) ? subject.StableId : "unknown-subject";
    }
}
