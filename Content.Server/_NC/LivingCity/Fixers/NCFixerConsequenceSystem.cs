// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Threading;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Projects terminal deal outcomes into the existing directed relationship and reputation authorities. Stable world
/// event identities make recovery safe: replaying a completed deal cannot apply its social result twice.
/// </summary>
public sealed partial class NCFixerConsequenceSystem : EntitySystem
{
    private static readonly ProtoId<NCFixerPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCReputationRegistrySystem _reputations = default!;

    private readonly ConcurrentDictionary<NCFixerDealId, byte> _inFlight = [];
    private long _completed;
    private long _failed;
    private string? _lastError;

    public int InFlightCount => _inFlight.Count;
    public long CompletedCount => Interlocked.Read(ref _completed);
    public long FailedCount => Interlocked.Read(ref _failed);
    public string? LastError => Volatile.Read(ref _lastError);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFixerAggregateChangedEvent>(OnFixerAggregateChanged);
    }

    private void OnFixerAggregateChanged(NCFixerAggregateChangedEvent args)
    {
        if (args.AggregateType != NCFixerRegistrySystem.DealAggregateType ||
            !Guid.TryParse(args.AggregateId, out var raw))
            return;

        _ = LoadAndApplyAsync(new NCFixerDealId(raw));
    }

    private async Task LoadAndApplyAsync(NCFixerDealId dealId)
    {
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal != null)
            await ApplyAsync(deal);
    }

    public async Task<bool> ApplyAsync(NCFixerDealRecord deal)
    {
        if (deal.Snapshot.Status is not (NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Breached) ||
            !_inFlight.TryAdd(deal.Snapshot.DealId, 0))
            return true;

        try
        {
            if (!TryResolveOutcome(deal.Snapshot, out var observer, out var subject, out var relationshipEffect,
                    out var reputationEffect, out var domain))
                return Fail(deal.Snapshot.DealId, "The persisted deal consequence policy is invalid.");

            var outcome = deal.Snapshot.Status == NCFixerDealStatus.Fulfilled ? "fulfilled" : "breached";
            var stable = $"{deal.Snapshot.DealId}.{outcome}.{subject}";
            var worldEventId = NCPersistentIdentity.Command("fixer-consequence", "world-event", stable);
            var correlationId = NCPersistentIdentity.Correlation("fixer-consequence", outcome, stable);
            var relationship = await _relationships.ApplyEffectAsync(new NCApplyRelationshipEffectRequest(
                NCPersistentIdentity.Command("fixer-consequence", "relationship", stable),
                observer,
                subject,
                relationshipEffect,
                worldEventId,
                1,
                deal.Snapshot.Fixer,
                correlationId,
                deal.Snapshot.LastAppliedEventId,
                "fixer-deal-consequence"));
            if (relationship.Status is not (NCRelationshipMutationStatus.Applied or
                    NCRelationshipMutationStatus.Duplicate))
                return Fail(deal.Snapshot.DealId, relationship.Error ?? "The relationship consequence failed.");

            var reputation = await _reputations.ApplyEffectAsync(new NCApplyReputationEffectRequest(
                NCPersistentIdentity.Command("fixer-consequence", "reputation", stable),
                subject,
                new NCReputationAudienceRequest(NCReputationChannel.Professional, default, domain),
                reputationEffect,
                worldEventId,
                1,
                deal.Snapshot.Fixer,
                correlationId,
                deal.Snapshot.LastAppliedEventId,
                "fixer-deal-consequence"));
            if (reputation.Status is not (NCReputationMutationStatus.Applied or NCReputationMutationStatus.Duplicate))
                return Fail(deal.Snapshot.DealId, reputation.Error ?? "The reputation consequence failed.");

            Interlocked.Increment(ref _completed);
            Volatile.Write(ref _lastError, null);
            return true;
        }
        catch (Exception exception)
        {
            return Fail(deal.Snapshot.DealId, exception.Message);
        }
        finally
        {
            _inFlight.TryRemove(deal.Snapshot.DealId, out _);
        }
    }

    private bool TryResolveOutcome(
        NCFixerDealSnapshot deal,
        out NCPartyRef observer,
        out NCPartyRef subject,
        out ProtoId<NCRelationshipEffectPrototype> relationshipEffect,
        out ProtoId<NCReputationEffectPrototype> reputationEffect,
        out ProtoId<NCReputationDomainPrototype> domain)
    {
        var defaults = _prototypes.TryIndex(DefaultPolicy, out NCFixerPolicyPrototype? policy)
            ? new NCFixerDealConsequences(policy.SuccessRelationshipEffectId, policy.BreachRelationshipEffectId,
                policy.SuccessReputationEffectId, policy.BreachReputationEffectId, policy.ReputationDomainId)
            : null;
        var consequences = deal.Plan.Consequences ?? defaults;
        if (consequences == null)
        {
            observer = default;
            subject = default;
            relationshipEffect = default;
            reputationEffect = default;
            domain = default;
            return false;
        }

        subject = deal.Status == NCFixerDealStatus.Breached
            ? deal.Plan.Legs.FirstOrDefault(value => value.Status is not (NCFixerDealLegStatus.Delivered or
                NCFixerDealLegStatus.Waived))?.From ?? deal.Seller
            : deal.Seller;
        observer = subject == deal.Buyer ? deal.Seller : deal.Buyer;
        if (!observer.IsValid || !subject.IsValid || observer == subject)
        {
            relationshipEffect = default;
            reputationEffect = default;
            domain = default;
            return false;
        }

        relationshipEffect = new ProtoId<NCRelationshipEffectPrototype>(
            deal.Status == NCFixerDealStatus.Fulfilled
                ? consequences.SuccessRelationshipEffectId
                : consequences.BreachRelationshipEffectId);
        reputationEffect = new ProtoId<NCReputationEffectPrototype>(
            deal.Status == NCFixerDealStatus.Fulfilled
                ? consequences.SuccessReputationEffectId
                : consequences.BreachReputationEffectId);
        domain = new ProtoId<NCReputationDomainPrototype>(consequences.ReputationDomainId);
        return _prototypes.HasIndex(relationshipEffect) && _prototypes.HasIndex(reputationEffect) &&
               _prototypes.HasIndex(domain);
    }

    private bool Fail(NCFixerDealId dealId, string error)
    {
        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastError, $"Fixer deal {dealId}: {error}");
        Log.Error(_lastError);
        return false;
    }
}
