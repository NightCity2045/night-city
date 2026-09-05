// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Cuffs;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public sealed partial class NCHumanDirectiveSystem
{
    private const string ZoneDirectiveAction = "human-execute-zone-directive";
    private const string PersonDirectiveAction = "human-execute-person-directive";
    private const int MaximumExpiriesPerTick = 32;

    [Dependency] private SharedCuffableSystem _cuffable = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;

        // Directives are removed from a bounded due queue; no city-wide actor scan runs in Update().
        for (var i = 0; i < MaximumExpiriesPerTick &&
             _expiries.TryPeek(out var expiry, out var dueTicks) && dueTicks <= now.Ticks; i++)
        {
            _expiries.Dequeue();
            if (_directives.TryGetValue(expiry.ActorId, out var current) &&
                current.RequestId == expiry.RequestId && current.ExpiresAt <= now)
            {
                FinishDirective(expiry.ActorId, expiry.RequestId, "nc-human-directive-expired");
            }
        }
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_directives.TryGetValue(args.ActorId, out var directive) || directive.ExpiresAt <= args.Now)
            return;

        var person = IsPersonDirective(directive.Execution);
        var subject = person
            ? new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, directive.TargetHandle)
            : new NCCognitionSubjectRef(NCCognitionReferenceKind.Zone, directive.TargetZone.ToString());
        if (!subject.IsValid)
            return;

        args.Sink.Add(new NCCognitionFact(
            person ? "human.directive.person-active" : "human.directive.zone-active",
            subject,
            NCCognitionDomain.Faction,
            "human-authorized-directive",
            Math.Max(1, directive.AcceptedAt.Ticks),
            NCCognitionValue.FromStableKey(directive.ActionId),
            directive.AcceptedAt,
            directive.ExpiresAt));
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (args.Materialized && _directives.TryGetValue(args.ActorId, out var directive))
            MirrorDirective(directive);
    }

    private void ExecuteDirective(NCHumanActionExecutionEvent args)
    {
        args.Handled = true;
        if (!_directives.TryGetValue(args.ActorId, out var directive) || directive.ExpiresAt <= args.Now)
        {
            args.Failure = "human-directive-not-active";
            return;
        }

        var person = IsPersonDirective(directive.Execution);
        if (person != (args.Action.ID == PersonDirectiveAction) ||
            !person && args.Action.ID != ZoneDirectiveAction)
        {
            FailDirective(directive, args, "human-directive-executor-mismatch");
            return;
        }

        if (person)
            ExecutePersonDirective(directive, args);
        else
            ExecuteZoneDirective(directive, args);
    }

    private void ExecuteZoneDirective(ActiveDirective directive, NCHumanActionExecutionEvent args)
    {
        if (!_actors.TryGetCached(directive.ActorId, out var actor))
        {
            FailDirective(directive, args, "human-directive-actor-unavailable");
            return;
        }

        var atTarget = actor.Snapshot.Location.CurrentZone == directive.TargetZone &&
                       actor.Snapshot.Location.TravelPhase is not (NCCityActorTravelPhase.Planned or
                           NCCityActorTravelPhase.InTransit);
        if (!atTarget)
        {
            // The schedule/CityGraph pipeline owns travel. Acknowledgement is released only once that authority
            // has actually accepted the commitment and changed the travel phase.
            if (actor.Snapshot.Location.TravelPhase is NCCityActorTravelPhase.Planned or
                NCCityActorTravelPhase.InTransit)
            {
                MarkStarted(directive, NCHumanDirectiveExecutionPhase.Traveling);
            }
            return;
        }

        switch (directive.Execution)
        {
            case NCHumanDirectiveExecutionKind.GoToZone:
            case NCHumanDirectiveExecutionKind.ReportToZone:
                MarkStarted(directive, NCHumanDirectiveExecutionPhase.Active);
                FinishDirective(directive.ActorId, directive.RequestId);
                args.Completed = true;
                return;
            case NCHumanDirectiveExecutionKind.WaitAtZone:
            case NCHumanDirectiveExecutionKind.GuardZone:
                if (args.EntityUid is not { } guard || !Exists(guard))
                {
                    MarkStarted(directive, NCHumanDirectiveExecutionPhase.Active);
                    return;
                }
                var hold = RunChild(directive, args, "human-hold-position", NCCognitionSubjectRef.None, 12);
                ApplyChildResult(directive, args, hold, NCHumanDirectiveExecutionPhase.Active, terminal: false);
                return;
            case NCHumanDirectiveExecutionKind.PatrolZone:
                if (args.EntityUid is not { } patrol || !Exists(patrol))
                {
                    // Abstract patrol remains represented by the authoritative commitment until materialization.
                    MarkStarted(directive, NCHumanDirectiveExecutionPhase.Active);
                    return;
                }
                var patrolTarget = new NCCognitionSubjectRef(
                    NCCognitionReferenceKind.Zone,
                    directive.TargetZone.ToString());
                var patrolAction = RunChild(directive, args, "human-patrol-zone", patrolTarget, 16);
                ApplyChildResult(directive, args, patrolAction,
                    NCHumanDirectiveExecutionPhase.Active, terminal: false);
                return;
            default:
                FailDirective(directive, args, "human-directive-zone-action-unsupported");
                return;
        }
    }

    private void ExecutePersonDirective(ActiveDirective directive, NCHumanActionExecutionEvent args)
    {
        if (args.EntityUid is not { } actor || !Exists(actor) ||
            directive.TargetEntity is not { } target || !Exists(target) || target == actor)
        {
            FailDirective(directive, args, "human-directive-person-unavailable");
            return;
        }
        if (directive.Execution == NCHumanDirectiveExecutionKind.EscortSubjectToZone &&
            !_cuffable.IsCuffed(target))
        {
            FailDirective(directive, args, "human-directive-escort-subject-not-restrained");
            return;
        }

        var targetRef = new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, directive.TargetHandle);
        switch (directive.Execution)
        {
            case NCHumanDirectiveExecutionKind.ApproachIssuer:
            {
                var child = RunChild(directive, args, "human-approach-person", targetRef, 10);
                ApplyChildResult(directive, args, child, NCHumanDirectiveExecutionPhase.Approaching,
                    terminal: true);
                return;
            }
            case NCHumanDirectiveExecutionKind.FollowIssuer:
            {
                var child = RunChild(directive, args, "human-follow-person", targetRef, 11);
                ApplyChildResult(directive, args, child, NCHumanDirectiveExecutionPhase.Active, terminal: false);
                return;
            }
            case NCHumanDirectiveExecutionKind.EscortSubjectToZone:
                ExecuteEscort(directive, args, targetRef);
                return;
            default:
                FailDirective(directive, args, "human-directive-person-action-unsupported");
                return;
        }
    }

    private void ExecuteEscort(
        ActiveDirective directive,
        NCHumanActionExecutionEvent args,
        NCCognitionSubjectRef target)
    {
        switch (directive.Phase)
        {
            case NCHumanDirectiveExecutionPhase.Accepted:
            case NCHumanDirectiveExecutionPhase.Approaching:
            {
                var approach = RunChild(directive, args, "human-approach-person", target, 10);
                if (!ApplyChildResult(directive, args, approach, NCHumanDirectiveExecutionPhase.Approaching,
                        terminal: false))
                    return;
                if (approach.Completed)
                    SetPhase(directive, NCHumanDirectiveExecutionPhase.Pulling);
                return;
            }
            case NCHumanDirectiveExecutionPhase.Pulling:
            {
                var pulling = RunChild(directive, args, "human-start-pulling-target", target, 13);
                if (!ApplyChildResult(directive, args, pulling, NCHumanDirectiveExecutionPhase.Pulling,
                        terminal: false))
                    return;
                if (pulling.Completed)
                    SetPhase(directive, NCHumanDirectiveExecutionPhase.Traveling);
                return;
            }
            case NCHumanDirectiveExecutionPhase.Traveling:
                if (!_actors.TryGetCached(directive.ActorId, out var actor) ||
                    actor.Snapshot.Location.CurrentZone != directive.TargetZone ||
                    actor.Snapshot.Location.TravelPhase is NCCityActorTravelPhase.Planned or
                        NCCityActorTravelPhase.InTransit)
                {
                    return;
                }
                var stop = RunChild(directive, args, "human-stop-pulling", NCCognitionSubjectRef.None, 14);
                ApplyChildResult(directive, args, stop, NCHumanDirectiveExecutionPhase.Active, terminal: true);
                return;
            case NCHumanDirectiveExecutionPhase.Active:
                FinishDirective(directive.ActorId, directive.RequestId);
                args.Completed = true;
                return;
        }
    }

    private NCHumanActionExecutionEvent RunChild(
        ActiveDirective directive,
        NCHumanActionExecutionEvent parent,
        string actionId,
        NCCognitionSubjectRef target,
        byte discriminator)
    {
        if (!_prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(actionId), out var action))
        {
            var missing = CreateMissingChild(directive, parent, actionId, target);
            missing.Failure = $"human-directive-child-action-missing:{actionId}";
            return missing;
        }

        var instanceId = DeterministicGuid(directive.RequestId, directive.ActorId.Value, discriminator);
        SetChild(directive, instanceId, actionId);
        var child = new NCHumanActionExecutionEvent(
            instanceId,
            parent.ActorId,
            parent.EntityUid,
            parent.IntentionId,
            parent.PlanId,
            parent.Intention,
            new NCCognitionPlanStep(actionId, actionId, target, [], false),
            target,
            action,
            directive.AcceptedAt,
            parent.Now);
        RaiseLocalEvent(child);
        if (!child.Handled && child.Failure == null)
            child.Failure = $"human-directive-child-action-unhandled:{actionId}";
        return child;
    }

    private static NCHumanActionExecutionEvent CreateMissingChild(
        ActiveDirective directive,
        NCHumanActionExecutionEvent parent,
        string actionId,
        NCCognitionSubjectRef target) =>
        new(Guid.Empty, parent.ActorId, parent.EntityUid, parent.IntentionId, parent.PlanId, parent.Intention,
            new NCCognitionPlanStep(actionId, actionId, target, [], false), target, parent.Action,
            directive.AcceptedAt, parent.Now);

    private bool ApplyChildResult(
        ActiveDirective directive,
        NCHumanActionExecutionEvent parent,
        NCHumanActionExecutionEvent child,
        NCHumanDirectiveExecutionPhase startedPhase,
        bool terminal)
    {
        if (!string.IsNullOrWhiteSpace(child.Failure))
        {
            FailDirective(directive, parent, child.Failure);
            return false;
        }

        var ownsPhysicalAction = child.Completed || parent.EntityUid is { } entity &&
            TryComp<NCHumanVanillaActionComponent>(entity, out var vanilla) &&
            vanilla.InstanceId == child.InstanceId && vanilla.OwnsSteering;
        if (ownsPhysicalAction)
            MarkStarted(directive, startedPhase);
        if (!terminal || !child.Completed)
            return true;

        FinishDirective(directive.ActorId, directive.RequestId);
        parent.Completed = true;
        return true;
    }

    private void MarkStarted(ActiveDirective directive, NCHumanDirectiveExecutionPhase phase)
    {
        SetPhase(directive, phase);
        if (!_pendingDialogues.Remove(directive.RequestId, out var pending) ||
            pending.ActorId != directive.ActorId)
            return;

        var response = pending.Response;
        if (pending.AppliedRecipients != pending.RequestedRecipients)
        {
            response = response with
            {
                SpeechActKey = "acceptance",
                RenderedText = Loc.GetString("nc-human-directive-partial-acknowledgement",
                    ("applied", pending.AppliedRecipients), ("requested", pending.RequestedRecipients)),
                Intent = null,
            };
        }
        _suggestions.TryDeliverDeferredDialogue(pending.ActorId, pending.Request.RequestId,
            pending.SessionId, pending.ConversationVersion, response);
    }

    private void SetPhase(ActiveDirective directive, NCHumanDirectiveExecutionPhase phase)
    {
        if (!_directives.TryGetValue(directive.ActorId, out var current) ||
            current.RequestId != directive.RequestId || current.Phase == phase)
            return;
        var updated = current with { Phase = phase };
        _directives[directive.ActorId] = updated;
        MirrorDirective(updated);
    }

    private void SetChild(ActiveDirective directive, Guid instanceId, string actionId)
    {
        if (!_directives.TryGetValue(directive.ActorId, out var current) ||
            current.RequestId != directive.RequestId ||
            current.ChildInstanceId == instanceId && current.ChildActionId == actionId)
            return;
        var updated = current with { ChildInstanceId = instanceId, ChildActionId = actionId };
        _directives[directive.ActorId] = updated;
        MirrorDirective(updated);
    }

    private void MirrorDirective(ActiveDirective directive)
    {
        if (!_runtime.TryGetEntity(directive.ActorId, out var entity) || !Exists(entity))
            return;
        var component = EnsureComp<NCHumanDirectiveComponent>(entity);
        component.RequestId = directive.RequestId;
        component.IssuedTo = directive.ActorId;
        component.CommitmentId = directive.CommitmentId;
        component.Action = directive.ActionId;
        component.Order = directive.Order;
        component.TargetZone = directive.TargetZone;
        component.AcceptedAt = directive.AcceptedAt;
        component.ExpiresAt = directive.ExpiresAt;
        component.TargetEntity = directive.TargetEntity;
        component.TargetHandle = directive.TargetHandle;
        component.Phase = directive.Phase;
    }

    private void FailDirective(ActiveDirective directive, NCHumanActionExecutionEvent args, string failure)
    {
        args.Failure = failure;
        FinishDirective(directive.ActorId, directive.RequestId, "nc-human-directive-execution-failed");
    }

    private void FinishDirective(NCActorId actorId, Guid requestId, string? failureLocId = null)
    {
        if (!_directives.TryGetValue(actorId, out var directive) || directive.RequestId != requestId)
            return;
        _directives.Remove(actorId);
        StopPhysicalDirective(directive);
        if (_runtime.TryGetEntity(actorId, out var entity) && Exists(entity) &&
            HasComp<NCHumanDirectiveComponent>(entity))
            RemCompDeferred<NCHumanDirectiveComponent>(entity);
        if (_clock.TryGetCurrentTime(out _, out var now))
            _cognition.ForceSchedule(actorId, now);
        _ = RemoveCommitmentAsync(actorId, directive.CommitmentId, requestId);

        if (!_pendingDialogues.Remove(requestId, out var pending) || failureLocId == null)
            return;
        var response = pending.Response with
        {
            SpeechActKey = "refusal",
            RenderedText = Loc.GetString(failureLocId),
            Intent = null,
        };
        _suggestions.TryDeliverDeferredDialogue(pending.ActorId, pending.Request.RequestId,
            pending.SessionId, pending.ConversationVersion, response);
    }

    private void StopPhysicalDirective(ActiveDirective directive)
    {
        if (directive.ChildInstanceId == Guid.Empty || directive.ChildActionId.Length == 0 ||
            !_runtime.TryGetEntity(directive.ActorId, out var entity) || !Exists(entity))
            return;
        RaiseLocalEvent(new NCHumanActionInterruptedEvent(
            directive.ChildInstanceId,
            directive.ActorId,
            entity,
            directive.ChildActionId,
            NCHumanActionInterruptKind.PlanChanged));
    }

    private async Task RemoveCommitmentAsync(
        NCActorId actorId,
        NCCityCommitmentId commitmentId,
        Guid causationId)
    {
        var current = await _actors.GetAsync(actorId, true);
        if (current == null)
            return;
        await _actors.RemoveCommitmentAsync(new NCCityActorRemoveCommitmentRequest(
            DeterministicGuid(causationId, actorId.Value, 9),
            actorId,
            current.AggregateVersion,
            commitmentId,
            causationId,
            causationId,
            "human-natural-directive"));
    }

    private static bool IsPersonDirective(NCHumanDirectiveExecutionKind execution) => execution is
        NCHumanDirectiveExecutionKind.ApproachIssuer or
        NCHumanDirectiveExecutionKind.FollowIssuer or
        NCHumanDirectiveExecutionKind.EscortSubjectToZone;
}
