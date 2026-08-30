// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Interaction;
using Content.Server._NC.LivingCity.Time;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Neutral boundary between an execution plan and authoritative gameplay. Built-in activation succeeds only when the
/// real Robust interaction event is handled; domain actions are delegated to typed ECS subscribers.
/// </summary>
public sealed partial class NCCityPhysicalActionResolutionSystem : EntitySystem
{
    [Dependency] private SharedInteractionSystem _interaction = default!;

    private long _attempts;
    private long _succeeded;
    private long _failed;
    private long _unavailable;
    private long _unhandled;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public NCCityPhysicalActionResult ResolveActivate(EntityUid actor, EntityUid target, bool complexInteractions)
    {
        _attempts++;
        if (!Exists(actor) || !Exists(target))
            return Count(new NCCityPhysicalActionResult(
                NCCityPhysicalActionKind.Activate,
                NCCityPhysicalActionOutcome.Unavailable,
                "action-entity-unavailable"));

        var succeeded = _interaction.InteractionActivate(
            actor,
            target,
            complexInteractions: complexInteractions);
        return Count(new NCCityPhysicalActionResult(
            NCCityPhysicalActionKind.Activate,
            succeeded ? NCCityPhysicalActionOutcome.Succeeded : NCCityPhysicalActionOutcome.Failed,
            succeeded ? null : "gameplay-activation-rejected"));
    }

    public NCCityPhysicalActionResult ResolveDomainAction(
        EntityUid actor,
        EntityUid target,
        NCCityPhysicalActionKind kind)
    {
        _attempts++;
        if (!Exists(actor) || !Exists(target))
            return Count(new NCCityPhysicalActionResult(kind, NCCityPhysicalActionOutcome.Unavailable,
                "action-entity-unavailable"));

        var attempt = new NCCityPhysicalActionAttemptEvent(
            actor,
            target,
            kind,
            false,
            NCCityPhysicalActionOutcome.Unhandled,
            null);
        RaiseLocalEvent(ref attempt);
        return Count(new NCCityPhysicalActionResult(
            kind,
            attempt.Handled ? attempt.Outcome : NCCityPhysicalActionOutcome.Unhandled,
            attempt.Failure));
    }

    public NCCityPhysicalActionResolutionCounters GetCounters() => new(
        _attempts,
        _succeeded,
        _failed,
        _unavailable,
        _unhandled);

    private NCCityPhysicalActionResult Count(NCCityPhysicalActionResult result)
    {
        switch (result.Outcome)
        {
            case NCCityPhysicalActionOutcome.Succeeded:
                _succeeded++;
                break;
            case NCCityPhysicalActionOutcome.Failed:
                _failed++;
                break;
            case NCCityPhysicalActionOutcome.Unavailable:
                _unavailable++;
                break;
            case NCCityPhysicalActionOutcome.Unhandled:
                _unhandled++;
                break;
        }
        return result;
    }

    private void Reset()
    {
        _attempts = 0;
        _succeeded = 0;
        _failed = 0;
        _unavailable = 0;
        _unhandled = 0;
    }
}
