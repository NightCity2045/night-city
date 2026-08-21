// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Adds bounded deal facts and handles typed cognition steps through the same public Fixer APIs used by players.
/// Neural or deterministic planners cannot bypass access, versions, escrow, or delivery validation.
/// </summary>
public sealed partial class NCFixerCognitionSystem : EntitySystem
{
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCFixerDealOrchestrationSystem _orchestration = default!;
    [Dependency] private NCFixerEscrowSystem _escrow = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCCognitionExecuteStepEvent>(OnExecuteStep);
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        var party = NCPartyRef.ForActor(args.ActorId);
        var deals = _fixers.GetCachedDealsForParty(party, 16);
        foreach (var deal in deals)
        {
            var actionable = deal.Snapshot.Fixer == party && deal.Snapshot.Status is
                NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.AwaitingHandoff;
            var subject = new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey,
                deal.Snapshot.DealId.ToString());
            args.Sink.Add(new NCCognitionFact("fixer.deal-active", subject, NCCognitionDomain.Economy,
                $"fixer-deal:{deal.Snapshot.DealId}", deal.AggregateVersion,
                NCCognitionValue.FromBoolean(true), args.Now, args.Now.Advance(TimeSpan.FromMinutes(2))));
            if (actionable)
            {
                args.Sink.Add(new NCCognitionFact("fixer.deal-actionable", subject, NCCognitionDomain.Economy,
                    $"fixer-deal:{deal.Snapshot.DealId}", deal.AggregateVersion,
                    NCCognitionValue.FromBoolean(true), args.Now, args.Now.Advance(TimeSpan.FromMinutes(2))));
            }
        }
    }

    private void OnExecuteStep(NCCognitionExecuteStepEvent args)
    {
        if (args.Step.ActionId != "manage-fixer-deal" ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !Guid.TryParse(args.Intention.Target.StableId, out var raw))
            return;
        var dealId = new NCFixerDealId(raw);
        var party = NCPartyRef.ForActor(args.ActorId);
        var deal = _fixers.GetCachedDealsForParty(party, 16).FirstOrDefault(value =>
            value.Snapshot.DealId == dealId);
        args.Handled = true;
        if (deal == null || deal.Snapshot.Fixer != party)
        {
            args.Failure = "fixer-deal-unavailable";
            return;
        }
        args.Completed = true;
        if (deal.Snapshot.Status == NCFixerDealStatus.EscrowFunded)
            _ = _orchestration.ActivateAsync(dealId, deal.AggregateVersion, party);
        else if (deal.Snapshot.Status == NCFixerDealStatus.AwaitingHandoff)
            _ = _escrow.SettleAsync(dealId, deal.AggregateVersion, party);
        else
            args.Failure = "fixer-deal-not-actionable";
    }
}
