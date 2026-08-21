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
    [Dependency] private NCSocialProfileRegistrySystem _profiles = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;

    public async Task<NCRedactedSocialContext?> BuildAsync(
        NCPartyRef owner,
        NCSocialContextPurpose purpose,
        NCConversationSessionId? conversationSessionId = null)
    {
        if (!owner.IsValid || !Enum.IsDefined(purpose) || !TryGetPolicy(out var policy))
            return null;

        var maximum = purpose switch
        {
            NCSocialContextPurpose.InternalReasoning => policy.InternalMaximumPrivacy,
            NCSocialContextPurpose.ConversationRendering => policy.ConversationMaximumPrivacy,
            NCSocialContextPurpose.PublicRendering => policy.PublicMaximumPrivacy,
            _ => NCMemoryPrivacy.Public,
        };
        if (purpose == NCSocialContextPurpose.ConversationRendering)
        {
            if (conversationSessionId is not { IsValid: true } sessionId ||
                !_conversations.TryGet(sessionId, out var session) || !session.Participants.Contains(owner))
            {
                return null;
            }
            maximum = (NCMemoryPrivacy) Math.Min((byte) maximum, (byte) session.MaximumDisclosure);
        }

        var memoryBookTask = _memories.GetAsync(owner);
        var memoriesTask = _memories.GetRelevantAsync(owner,
            new NCMemoryQuery([], [], [], maximum, policy.MaximumItemsPerDomain));
        var rumorBookTask = _rumors.GetAsync(owner);
        var rumorsTask = _rumors.GetKnownAsync(owner, maximum, policy.MaximumItemsPerDomain);
        var obligationsTask = _obligations.GetForPartyAsync(owner, policy.MaximumItemsPerDomain, true);
        NCSocialProfileRecord? profile = null;
        if (owner.TryGetActor(out var actorId))
            profile = await _profiles.GetAsync(actorId);
        await Task.WhenAll(memoryBookTask, memoriesTask, rumorBookTask, rumorsTask, obligationsTask);

        var facts = new List<NCRedactedSocialFact>(policy.MaximumItemsPerDomain * 3);
        foreach (var item in await memoriesTask)
        {
            if (item.Episode is { } episode)
            {
                facts.Add(new NCRedactedSocialFact(
                    "memory",
                    episode.EventTypeKey,
                    episode.Participants.FirstOrDefault().ToString(),
                    episode.Confidence / 1000f,
                    episode.Privacy));
            }
            else if (item.Summary is { } summary)
            {
                facts.Add(new NCRedactedSocialFact(
                    "memory-summary",
                    summary.ConsolidationKey,
                    summary.Participants.FirstOrDefault().ToString(),
                    summary.MinimumConfidence / 1000f,
                    summary.Privacy));
            }
        }
        foreach (var rumor in await rumorsTask)
        {
            facts.Add(new NCRedactedSocialFact(
                "rumor",
                rumor.TopicKey,
                rumor.Subject.StableId,
                rumor.Confidence / 1000f,
                rumor.Privacy));
        }
        var obligations = await obligationsTask;
        foreach (var obligation in obligations.Where(value => value.Snapshot.Privacy <= maximum))
        {
            var snapshot = obligation.Snapshot;
            var counterparty = snapshot.Debtor == owner ? snapshot.Creditor : snapshot.Debtor;
            facts.Add(new NCRedactedSocialFact(
                "obligation",
                snapshot.Terms.PurposeKey,
                counterparty.ToString(),
                1f,
                snapshot.Privacy));
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
            profile?.Snapshot.Speech.ForbiddenTopics ?? [],
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

    private bool TryGetPolicy(out NCSocialContextPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    private static bool Valid(NCSocialContextPolicyPrototype value) =>
        value.MaximumItemsPerDomain is > 0 and <= 32 && Enum.IsDefined(value.InternalMaximumPrivacy) &&
        Enum.IsDefined(value.ConversationMaximumPrivacy) && Enum.IsDefined(value.PublicMaximumPrivacy) &&
        value.ConversationMaximumPrivacy <= value.InternalMaximumPrivacy &&
        value.PublicMaximumPrivacy <= value.ConversationMaximumPrivacy;
}
