// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Pure Fact→Belief→Utility→BDI pipeline. It evaluates immutable inputs and never mutates ECS or a domain authority.
/// </summary>
public static class NCCognitionDecisionEngine
{
    public static NCBeliefSnapshot[] ProjectBeliefs(
        NCActorId actorId,
        IReadOnlyList<NCBeliefSnapshot> previous,
        IReadOnlyList<NCCognitionFact> facts,
        NCCognitionDecisionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        var result = new Dictionary<Guid, NCBeliefSnapshot>();
        foreach (var fact in facts)
        {
            var beliefId = NCPersistentIdentity.CognitionBelief(
                actorId, fact.Key, fact.Subject.StableId, fact.Source);
            var old = previous.FirstOrDefault(value => value.BeliefId == beliefId);
            if (old != null && old.SourceVersion == fact.SourceVersion && old.Value == fact.Value &&
                old.ExpiresAt > now && old.Freshness == NCBeliefFreshness.Current)
            {
                result[beliefId] = old;
                continue;
            }
            result[beliefId] = new NCBeliefSnapshot(
                beliefId,
                fact.Key,
                fact.Subject,
                fact.Domain,
                fact.Source,
                fact.SourceVersion,
                fact.Value,
                1f,
                NCBeliefFreshness.Current,
                fact.ObservedAt,
                fact.ExpiresAt,
                (old?.Revision ?? 0) + 1);
        }

        foreach (var old in previous)
        {
            if (result.ContainsKey(old.BeliefId) || old.ExpiresAt <= now)
                continue;
            var age = now.ElapsedSince(old.ObservedAt);
            var freshness = age >= policy.BeliefAgingTime
                ? NCBeliefFreshness.Stale
                : NCBeliefFreshness.Aging;
            var confidenceMultiplier = freshness == NCBeliefFreshness.Stale
                ? policy.StaleConfidenceMultiplier
                : policy.AgingConfidenceMultiplier;
            var confidence = Math.Clamp(old.Confidence * confidenceMultiplier, 0f, 1f);
            result[old.BeliefId] = old with
            {
                Confidence = confidence,
                Freshness = freshness,
                Revision = old.Revision + 1,
            };
        }

        var conflicting = result.Values
            .GroupBy(value => (value.Key, value.Subject))
            .Where(group => group.Select(value => value.Value).Distinct().Skip(1).Any())
            .SelectMany(group => group.Select(value => value.BeliefId))
            .ToHashSet();
        foreach (var beliefId in conflicting)
            result[beliefId] = result[beliefId] with { Freshness = NCBeliefFreshness.Disputed };

        return result.Values
            .OrderBy(value => value.Freshness)
            .ThenByDescending(value => value.Confidence)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .ThenBy(value => value.BeliefId)
            .Take(policy.MaximumBeliefs)
            .ToArray();
    }

    public static NCCognitionCapabilityCandidate[] EvaluateCapabilities(
        IPrototypeManager prototypes,
        NCCognitionCapabilityProfilePrototype profile,
        NCCognitionDecisionPolicyPrototype policy,
        IReadOnlyList<NCBeliefSnapshot> beliefs) =>
        EvaluateCapabilities(prototypes, profile.Capabilities, policy, beliefs);

    public static NCCognitionCapabilityCandidate[] EvaluateCapabilities(
        IPrototypeManager prototypes,
        IReadOnlyList<ProtoId<NCCognitionCapabilityPrototype>> capabilityIds,
        NCCognitionDecisionPolicyPrototype policy,
        IReadOnlyList<NCBeliefSnapshot> beliefs)
    {
        var candidates = new List<NCCognitionCapabilityCandidate>();
        foreach (var capabilityId in capabilityIds.Distinct())
        {
            if (!prototypes.TryIndex(capabilityId, out var capability) || capability == null)
                continue;
            if (!capability.RequiredBeliefs.All(key => HasTruthyBelief(beliefs, key, policy)) ||
                capability.ForbiddenBeliefs.Any(key => HasTruthyBelief(beliefs, key, policy)))
                continue;

            var utility = capability.BaseUtility;
            var personalityModifier = BestBelief(
                beliefs,
                $"social.utility-modifier.{capability.ID}",
                policy);
            if (personalityModifier != null)
                utility += Numeric(personalityModifier.Value) * personalityModifier.Confidence;
            var valid = true;
            foreach (var consideration in capability.Considerations)
            {
                var belief = BestBelief(beliefs, consideration.BeliefKey, policy);
                if (belief == null && consideration.Required)
                {
                    valid = false;
                    break;
                }
                if (!prototypes.TryIndex(consideration.Curve, out var curve) || curve == null)
                {
                    valid = false;
                    break;
                }
                var input = belief == null ? consideration.MissingInput : Numeric(belief.Value);
                var confidence = belief?.Confidence ?? 1f;
                utility += EvaluateCurve(curve, input) * consideration.Weight * confidence;
            }
            if (!valid || !float.IsFinite(utility))
                continue;

            var neuralTarget = BestBelief(beliefs, $"neural.target.{capability.ID}", policy);
            var target = neuralTarget?.Subject.IsValid == true
                ? neuralTarget.Subject
                : beliefs
                .Where(value => capability.RequiredBeliefs.Contains(value.Key) && value.Subject.IsValid)
                .OrderByDescending(value => value.Confidence)
                .Select(value => value.Subject)
                .FirstOrDefault();
            candidates.Add(new NCCognitionCapabilityCandidate(
                capabilityId,
                target,
                utility,
                capability.Priority,
                capability.TaskKind,
                capability.PreferredPlanner,
                capability.FallbackPlanner,
                capability.InterruptsRoutine,
                capability.RequiresMaterialization));
        }
        return candidates
            .OrderByDescending(value => value.Priority)
            .ThenByDescending(value => value.Utility)
            .ThenBy(value => value.Capability.Id, StringComparer.Ordinal)
            .Take(policy.MaximumDesires)
            .ToArray();
    }

    public static (NCCognitionDesireSnapshot[] Desires, NCCognitionIntentionSnapshot? Intention, bool Switched)
        SelectIntention(
            NCActorId actorId,
            NCCognitionIntentionSnapshot? current,
            IReadOnlyList<NCCognitionCapabilityCandidate> candidates,
            NCCognitionDecisionPolicyPrototype policy,
            NCActiveWorldTime now,
            long revision)
    {
        var desires = candidates.Select(candidate => new NCCognitionDesireSnapshot(
                NCPersistentIdentity.CognitionDesire(actorId, candidate.Capability.Id),
                candidate.Capability.Id,
                candidate.Target,
                candidate.TaskKind,
                candidate.Utility,
                candidate.Priority,
                now,
                now.Advance(policy.IntentionLifetime)))
            .ToArray();
        if (candidates.Count == 0)
            return (desires, null, current != null);

        var best = candidates[0];
        var currentCandidate = current == null
            ? null
            : candidates.FirstOrDefault(value => value.Capability.Id == current.CapabilityId);
        var keepCurrent = current != null && current.ExpiresAt > now && currentCandidate != null;
        if (keepCurrent && now < current!.MinimumCommitmentUntil &&
            !(best.InterruptsRoutine && best.Priority > current.Priority))
        {
            return (desires, current, false);
        }
        if (keepCurrent && best.Capability.Id != current!.CapabilityId &&
            best.Utility < currentCandidate!.Utility + policy.SwitchUtilityDelta + policy.SwitchCost &&
            !(best.InterruptsRoutine && best.Priority > current.Priority))
        {
            return (desires, current, false);
        }
        if (keepCurrent && best.Capability.Id == current!.CapabilityId)
            return (desires, current, false);

        var intentionRevision = Math.Max(1, revision);
        var intention = new NCCognitionIntentionSnapshot(
            NCPersistentIdentity.CognitionIntention(actorId, best.Capability.Id, intentionRevision),
            best.Capability.Id,
            best.Target,
            best.TaskKind,
            best.Utility,
            1f,
            best.Priority,
            now,
            now.Advance(policy.MinimumCommitment),
            now.Advance(policy.IntentionLifetime),
            intentionRevision);
        return (desires, intention, current?.IntentionId != intention.IntentionId);
    }

    public static float EvaluateCurve(NCCognitionUtilityCurvePrototype curve, float input)
    {
        if (!float.IsFinite(input) || curve.MaximumInput <= curve.MinimumInput)
            return curve.MinimumOutput;
        var normalized = Math.Clamp(
            (input - curve.MinimumInput) / (curve.MaximumInput - curve.MinimumInput), 0f, 1f);
        var shaped = curve.Kind switch
        {
            NCCognitionUtilityCurveKind.Linear => normalized,
            NCCognitionUtilityCurveKind.InverseLinear => 1f - normalized,
            NCCognitionUtilityCurveKind.Step => input >= curve.Threshold ? 1f : 0f,
            NCCognitionUtilityCurveKind.Bell => 1f - Math.Abs(normalized * 2f - 1f),
            _ => 0f,
        };
        return curve.MinimumOutput + (curve.MaximumOutput - curve.MinimumOutput) * shaped;
    }

    private static NCBeliefSnapshot? BestBelief(
        IReadOnlyList<NCBeliefSnapshot> beliefs,
        string key,
        NCCognitionDecisionPolicyPrototype policy) => beliefs
        .Where(value => value.Key == key && value.Confidence >= policy.MinimumBeliefConfidence &&
                        value.Freshness != NCBeliefFreshness.Disputed)
        .OrderBy(value => value.Freshness)
        .ThenByDescending(value => value.Confidence)
        .ThenByDescending(value => value.SourceVersion)
        .FirstOrDefault();

    private static bool HasTruthyBelief(
        IReadOnlyList<NCBeliefSnapshot> beliefs,
        string key,
        NCCognitionDecisionPolicyPrototype policy)
    {
        var belief = BestBelief(beliefs, key, policy);
        return belief != null && Numeric(belief.Value) > 0f;
    }

    private static float Numeric(NCCognitionValue value) => value.Kind switch
    {
        NCCognitionValueKind.Boolean => value.Boolean ? 1f : 0f,
        NCCognitionValueKind.Number => value.Number,
        NCCognitionValueKind.StableKey => string.IsNullOrWhiteSpace(value.StableKey) ? 0f : 1f,
        _ => 0f,
    };
}
