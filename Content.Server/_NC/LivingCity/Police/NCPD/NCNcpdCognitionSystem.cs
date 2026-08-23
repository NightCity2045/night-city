// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Systems;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared.Chat;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Bridges NCPD cognition into ordinary gameplay systems. Neural output can select one of these procedures and a
/// perceived subject handle, but only observed incidents, real movement, speech, restraints and pulling can succeed.
/// </summary>
public sealed partial class NCNcpdCognitionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physicalExecution = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private PullingSystem _pulling = default!;

    private readonly Dictionary<EntityUid, RestraintAttempt> _restraintAttempts = [];
    private readonly HashSet<EntityUid> _policeSteering = [];
    private readonly HashSet<EntityUid> _suspendedHumanHtn = [];
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
        SubscribeLocalEvent<NCCognitionExecuteStepEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCNcpdOfficerComponent, ComponentShutdown>(OnOfficerShutdown);
    }

    /// <summary>
    /// Adds the professional police interpretation to an incident already witnessed by universal human perception.
    /// The original visibility query and damage subscription remain centralized in NCCityLocalPerceptionSystem.
    /// </summary>
    private void OnIncidentObserved(
        NCCityLocalIncidentObservedEvent args)
    {
        if (args.Kind is not (NCCityLocalPerceivedEventKind.Assault or NCCityLocalPerceivedEventKind.Killing) ||
            !TryComp<NCNcpdOfficerComponent>(args.Observer, out var officer) ||
            !TryGetPolicy(args.Observer, out var policy))
        {
            return;
        }

        if (_perception.ObserveSuspect(
                args.Observer,
                args.Actor,
                args.ObservedAt,
                policy.SuspectObservationLifetime))
        {
            // A witnessed incident is immediate and must not wait for the routine cognition cadence.
            _cognition.ForceSchedule(officer.ActorId, args.ObservedAt);
        }
    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_runtime.TryGetEntity(args.ActorId, out var entityUid) ||
            !TryComp<NCNcpdOfficerComponent>(entityUid, out _) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception))
        {
            return;
        }

        args.Sink.Add(new NCCognitionFact(
            "ncpd.on-duty",
            default,
            NCCognitionDomain.Crime,
            "ncpd-officer-projection",
            Math.Max(1, perception.Revision),
            NCCognitionValue.FromBoolean(true),
            args.Now,
            args.Now.Advance(TimeSpan.FromMinutes(1))));
    }

    private void OnExecuteStep(NCCognitionExecuteStepEvent args)
    {
        if (!args.Step.ActionId.StartsWith("ncpd-", StringComparison.Ordinal) ||
            args.EntityUid is not { } officer || !Exists(officer) ||
            !TryComp<NCNcpdOfficerComponent>(officer, out _) || !TryGetPolicy(officer, out var policy))
        {
            return;
        }

        args.Handled = true;
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            args.Intention.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Intention.Target.StableId, now, out var suspect))
        {
            StopPoliceSteering(officer);
            args.Failure = "ncpd-suspect-no-longer-observed";
            return;
        }

        switch (args.Step.ActionId)
        {
            case "ncpd-assess-scene":
                args.Completed = true;
                return;
            case "ncpd-report-incident":
                ReportIncident(officer, args.Intention.Target.StableId, now);
                args.Completed = true;
                return;
            case "ncpd-pursue-suspect":
                args.Completed = Approach(officer, suspect, policy.VerbalOrderRange);
                return;
            case "ncpd-issue-lawful-order":
                if (!_interaction.InRangeUnobstructed(officer, suspect, policy.VerbalOrderRange))
                    return;
                _chat.TrySendInGameICMessage(
                    officer,
                    Loc.GetString(policy.VerbalOrderLocKey),
                    InGameICChatType.Speak,
                    hideChat: false);
                args.Completed = true;
                return;
            case "ncpd-restrain-suspect":
                ExecuteRestraint(officer, suspect, policy, now, args);
                return;
            case "ncpd-begin-escort":
                if (!_cuffable.IsCuffed(suspect))
                {
                    args.Failure = "ncpd-suspect-not-restrained";
                    return;
                }
                args.Completed = _pulling.TryStartPull(officer, suspect);
                if (!args.Completed)
                    args.Failure = "ncpd-escort-rejected-by-pulling-system";
                return;
        }

        args.Failure = $"ncpd-action-unsupported:{args.Step.ActionId}";
    }

    private bool Approach(EntityUid officer, EntityUid suspect, float range)
    {
        if (_transform.InRange(Transform(officer).Coordinates, Transform(suspect).Coordinates, range))
        {
            StopPoliceSteering(officer);
            return true;
        }

        // Profession code temporarily owns locomotion, but the actor keeps the same universal human HTN. Pausing
        // that root avoids two NPCSteering writers until this concrete police step has reached its target.
        SuspendHumanHtn(officer);
        _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        // Entity-relative coordinates follow a moving suspect while vanilla steering performs collision/path checks.
        if (!_steeringLease.Acquire(officer, "ncpd-physical-action"))
        {
            ResumeHumanHtn(officer);
            _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
            return false;
        }
        _policeSteering.Add(officer);
        _steering.Register(officer, new EntityCoordinates(suspect, Vector2.Zero));
        return false;
    }

    private void ExecuteRestraint(
        EntityUid officer,
        EntityUid suspect,
        NCNcpdUseOfForcePolicyPrototype policy,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        NCCognitionExecuteStepEvent args)
    {
        if (_cuffable.IsCuffed(suspect))
        {
            _restraintAttempts.Remove(officer);
            args.Completed = true;
            return;
        }
        if (!Approach(officer, suspect, policy.RestraintRange))
            return;
        if (_restraintAttempts.TryGetValue(officer, out var pending) && pending.Suspect == suspect &&
            now < pending.RetryAt)
        {
            return;
        }
        if (!TryReadyRestraints(officer, out var restraints))
        {
            args.Failure = "ncpd-restraints-unavailable";
            return;
        }

        _restraintAttempts[officer] = new RestraintAttempt(suspect, now.Advance(policy.RestraintRetryDelay));
        if (!_cuffable.TryCuffing(officer, suspect, restraints))
            args.Failure = "ncpd-restraint-rejected-by-cuff-system";
    }

    private bool TryReadyRestraints(EntityUid officer, out EntityUid restraints)
    {
        foreach (var held in _hands.EnumerateHeld(officer))
        {
            if (HasComp<HandcuffComponent>(held))
            {
                restraints = held;
                return true;
            }
        }

        if (_inventory.TryGetSlotEntity(officer, "belt", out var belt) &&
            TryComp<StorageComponent>(belt, out var storage))
        {
            foreach (var stored in storage.Container.ContainedEntities.ToArray())
            {
                if (HasComp<HandcuffComponent>(stored) && _hands.TryPickupAnyHand(officer, stored))
                {
                    restraints = stored;
                    return true;
                }
            }
        }

        restraints = EntityUid.Invalid;
        return false;
    }

    private void ReportIncident(
        EntityUid officer,
        string suspectHandle,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now)
    {
        var report = EnsureComp<NCNcpdIncidentReportComponent>(officer);
        report.ReportId = Guid.NewGuid();
        report.SuspectHandle = suspectHandle;
        report.ReportedAt = now;
        RaiseLocalEvent(new NCNcpdIncidentReportedEvent(
            report.ReportId,
            officer,
            suspectHandle,
            now));
    }

    private bool TryGetPolicy(EntityUid officer, out NCNcpdUseOfForcePolicyPrototype policy)
    {
        policy = default!;
        return TryComp<NCNcpdOfficerComponent>(officer, out var component) &&
               _prototypes.TryIndex(component.RoleProfile, out NCNcpdRoleProfilePrototype? role) && role != null &&
               _prototypes.TryIndex(role.ForcePolicy, out policy!);
    }

    private void OnOfficerShutdown(Entity<NCNcpdOfficerComponent> officer, ref ComponentShutdown args)
    {
        _restraintAttempts.Remove(officer.Owner);
        StopPoliceSteering(officer.Owner, false);
        _suspendedHumanHtn.Remove(officer.Owner);
    }

    private void StopPoliceSteering(EntityUid officer, bool restoreHumanHtn = true)
    {
        if (_policeSteering.Remove(officer) && Exists(officer))
        {
            _steering.Unregister(officer);
            _steeringLease.Release(officer, "ncpd-physical-action");
        }
        if (!restoreHumanHtn)
            return;
        _physicalExecution.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
        ResumeHumanHtn(officer);
    }

    private void SuspendHumanHtn(EntityUid officer)
    {
        if (_suspendedHumanHtn.Contains(officer) ||
            !TryComp<HTNComponent>(officer, out var htn) || !htn.Enabled)
        {
            return;
        }
        _htn.SetHTNEnabled((officer, htn), false);
        _suspendedHumanHtn.Add(officer);
    }

    private void ResumeHumanHtn(EntityUid officer)
    {
        if (!_suspendedHumanHtn.Remove(officer) || !Exists(officer) ||
            !TryComp<HTNComponent>(officer, out var htn))
        {
            return;
        }
        // The component's own YAML cooldown is the single tuning source for every profession.
        _htn.SetHTNEnabled((officer, htn), true, htn.PlanCooldown);
    }

    private readonly record struct RestraintAttempt(
        EntityUid Suspect,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime RetryAt);
}

/// <summary>Typed dispatch boundary emitted after a physical officer reports an observed incident.</summary>
public sealed class NCNcpdIncidentReportedEvent(
    Guid reportId,
    EntityUid officer,
    string suspectHandle,
    Content.Shared._NC.Persistence.Time.NCActiveWorldTime reportedAt) : EntityEventArgs
{
    public Guid ReportId { get; } = reportId;
    public EntityUid Officer { get; } = officer;
    public string SuspectHandle { get; } = suspectHandle;
    public Content.Shared._NC.Persistence.Time.NCActiveWorldTime ReportedAt { get; } = reportedAt;
}
