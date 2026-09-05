// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.Components;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.CombatMode;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Owns the bounded NCPD force lifecycle around vanilla combat. It grants hostility only for an observed lawful
/// claim and removes that hostility immediately when cuffs, compliance or mob state prove that force must stop.
/// Target-local faction trackers avoid city-wide entity scans.
/// </summary>
public sealed partial class NCNcpdUseOfForceSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NpcFactionSystem _factions = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private SharedCombatModeSystem _combatMode = default!;

    private long _forceStarted;
    private long _forceStopped;
    private long _surrenderStops;
    private long _incapacitationStops;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CuffableComponent, CuffedStateChangeEvent>(OnCuffedStateChanged);
        // Broadcast subscription avoids colliding with vanilla component-specific MobState handlers.
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<NCNcpdSuspectClaimComponent, ComponentShutdown>(OnClaimShutdown);
    }

    public NCNcpdUseOfForceCounters GetCounters() => new(
        _forceStarted,
        _forceStopped,
        _surrenderStops,
        _incapacitationStops);

    /// <summary>
    /// Makes a concrete suspect hostile through the vanilla faction exception API. The policy-selected force level
    /// remains data on the claim; weapon choice and attacks continue to be performed by vanilla NPC combat.
    /// </summary>
    public bool Begin(EntityUid officer, EntityUid suspect, NCNcpdForceLevel level)
    {
        if (!Exists(officer) || !Exists(suspect) || level < NCNcpdForceLevel.NonLethal ||
            !TryComp<NCNcpdOfficerComponent>(officer, out _) || IsNeutralized(suspect))
        {
            return false;
        }

        if (!_factions.GetHostiles(officer).Contains(suspect))
        {
            _factions.AggroEntity(officer, suspect);
            _forceStarted++;
        }
        return true;
    }

    /// <summary>
    /// Ends force against one suspect without touching any unrelated combat target held by the same officer.
    /// </summary>
    public bool End(
        EntityUid officer,
        EntityUid suspect,
        NCNcpdSuspectStatus status,
        NCNcpdForceLevel remainingAuthority,
        NCActiveWorldTime now,
        string reason)
    {
        if (!Exists(officer) || !TryComp<NCNcpdOfficerComponent>(officer, out var officerComponent))
            return false;

        var stopAllowed = TryGetPolicy(officer, out var policy) && status switch
        {
            NCNcpdSuspectStatus.Complying or NCNcpdSuspectStatus.Restrained => policy.StopForceOnSurrender,
            NCNcpdSuspectStatus.Incapacitated => policy.StopForceOnIncapacitation,
            _ => true,
        };
        var wasHostile = stopAllowed && Exists(suspect) && _factions.GetHostiles(officer).Contains(suspect);
        if (wasHostile)
            _factions.DeAggroEntity(officer, suspect);

        var removedCombat = stopAllowed && RemoveCombatTarget(officer, suspect);
        if (TryComp<NCNcpdSuspectClaimComponent>(officer, out var claim) && claim.Suspect == suspect)
        {
            claim.Status = status;
            claim.ForceLevel = remainingAuthority;
            claim.UpdatedAt = now;
            claim.ResultReason = reason;
        }

        if (wasHostile || removedCombat)
            _forceStopped++;
        _cognition.ForceSchedule(officerComponent.ActorId, now);
        return wasHostile || removedCombat;
    }

    private void OnCuffedStateChanged(Entity<CuffableComponent> entity, ref CuffedStateChangeEvent args)
    {
        if (!_cuffable.IsCuffed(entity.Owner, requireFullyCuffed: false) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return;
        }

        _surrenderStops += EndTrackedForce(entity.Owner, NCNcpdSuspectStatus.Restrained,
            NCNcpdForceLevel.Restraint, now, "suspect-restrained");
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive || !_clock.TryGetCurrentTime(out _, out var now))
            return;

        _incapacitationStops += EndTrackedForce(args.Target, NCNcpdSuspectStatus.Incapacitated,
            NCNcpdForceLevel.Presence, now, "suspect-incapacitated");
    }

    private int EndTrackedForce(
        EntityUid suspect,
        NCNcpdSuspectStatus status,
        NCNcpdForceLevel remainingAuthority,
        NCActiveWorldTime now,
        string reason)
    {
        if (!TryComp<FactionExceptionTrackerComponent>(suspect, out var tracker))
            return 0;

        var stopped = 0;
        // The tracker is local to this suspect and is copied because DeAggro mutates it.
        foreach (var officer in tracker.Entities.ToArray())
        {
            if (!TryGetPolicy(officer, out var policy) ||
                status == NCNcpdSuspectStatus.Restrained && !policy.StopForceOnSurrender ||
                status == NCNcpdSuspectStatus.Incapacitated && !policy.StopForceOnIncapacitation)
            {
                continue;
            }

            if (End(officer, suspect, status, remainingAuthority, now, reason))
                stopped++;
        }
        return stopped;
    }

    private bool RemoveCombatTarget(EntityUid officer, EntityUid suspect)
    {
        var removed = false;
        var hasOtherTarget = false;
        if (TryComp<NPCMeleeCombatComponent>(officer, out var melee))
        {
            if (melee.Target == suspect)
            {
                RemCompDeferred<NPCMeleeCombatComponent>(officer);
                removed = true;
            }
            else
                hasOtherTarget = true;
        }
        if (TryComp<NPCRangedCombatComponent>(officer, out var ranged))
        {
            if (ranged.Target == suspect)
            {
                RemCompDeferred<NPCRangedCombatComponent>(officer);
                removed = true;
            }
            else
                hasOtherTarget = true;
        }

        if (removed && !hasOtherTarget && TryComp<CombatModeComponent>(officer, out var combat))
            _combatMode.SetInCombatMode(officer, false, combat);
        return removed;
    }

    private void OnClaimShutdown(Entity<NCNcpdSuspectClaimComponent> entity, ref ComponentShutdown args)
    {
        if (!entity.Comp.Suspect.IsValid() || !Exists(entity.Owner) || !Exists(entity.Comp.Suspect))
            return;
        _factions.DeAggroEntity(entity.Owner, entity.Comp.Suspect);
        RemoveCombatTarget(entity.Owner, entity.Comp.Suspect);
    }

    private bool TryGetPolicy(EntityUid officer, out NCNcpdUseOfForcePolicyPrototype policy)
    {
        policy = default!;
        return TryComp<NCNcpdOfficerComponent>(officer, out var component) &&
               _prototypes.TryIndex(component.RoleProfile, out NCNcpdRoleProfilePrototype? role) && role != null &&
               _prototypes.TryIndex(role.ForcePolicy, out policy!);
    }

    private bool IsNeutralized(EntityUid suspect) =>
        _cuffable.IsCuffed(suspect, requireFullyCuffed: false) ||
        TryComp<MobStateComponent>(suspect, out var state) && state.CurrentState != MobState.Alive;
}
