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
    [Dependency] private NCFixerDealBoardSystem _dealBoard = default!;
    private readonly Dictionary<Guid, Task<FixerActionResult>> _pending = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        var party = NCPartyRef.ForActor(args.ActorId);
        var deals = _fixers.GetCachedDealsForParty(party, 16);
        foreach (var deal in deals)
        {
            var actionable = IsActionable(deal.Snapshot, party);
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

    private void OnExecuteStep(NCHumanActionExecutionEvent args)
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
        if (!_pending.TryGetValue(args.InstanceId, out var operation))
        {
            if (!IsActionable(deal.Snapshot, party))
            {
                args.Failure = "fixer-deal-not-actionable";
                return;
            }
            operation = ExecuteAsync(deal, party);
            _pending[args.InstanceId] = operation;
        }
        if (!operation.IsCompleted)
            return;

        _pending.Remove(args.InstanceId);
        var result = operation.GetAwaiter().GetResult();
        if (result.Success)
            args.Completed = true;
        else
            args.Failure = result.Error ?? "fixer-deal-mutation-rejected";
    }

    private async Task<FixerActionResult> ExecuteAsync(NCFixerDealRecord deal, NCPartyRef party)
    {
        try
        {
            NCFixerMutationStatus status;
            string? error;
            if (deal.Snapshot.Status == NCFixerDealStatus.EscrowFunded)
            {
                var result = await _orchestration.ActivateAsync(deal.Snapshot.DealId, deal.AggregateVersion, party);
                status = result.Status;
                error = result.Error;
            }
            else if (deal.Snapshot.Status == NCFixerDealStatus.InProgress)
            {
                var result = await _dealBoard.ProgressServiceDealAsync(deal, party);
                status = result.Deal == null ? NCFixerMutationStatus.Rejected : NCFixerMutationStatus.Applied;
                error = result.Error;
            }
            else
            {
                var result = await _escrow.SettleAsync(deal.Snapshot.DealId, deal.AggregateVersion, party);
                status = result.Status;
                error = result.Error;
            }
            return new FixerActionResult(status is NCFixerMutationStatus.Applied or NCFixerMutationStatus.Duplicate,
                error);
        }
        catch (Exception exception)
        {
            return new FixerActionResult(false, $"fixer-deal-exception:{exception.GetType().Name}");
        }
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId == "manage-fixer-deal")
            _pending.Remove(args.InstanceId);
    }

    private static bool IsActionable(NCFixerDealSnapshot deal, NCPartyRef party) =>
        deal.Fixer == party &&
        (deal.Status is NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.AwaitingHandoff ||
         deal.Status == NCFixerDealStatus.InProgress && deal.Seller == party &&
         deal.Plan.Legs.Any(value => value.Kind == NCFixerDealLegKind.Service &&
             value.MarketRequestId.HasValue));

    private readonly record struct FixerActionResult(bool Success, string? Error);
}
