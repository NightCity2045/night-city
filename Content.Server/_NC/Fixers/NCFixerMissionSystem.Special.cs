// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.Bank;
using Content.Shared._NC.Fixers;
using Content.Shared.Mind.Components;

namespace Content.Server._NC.Fixers;

/// <summary>Recon, talks that must stay peaceful, and laundering dirty money through a shell company.</summary>
public sealed partial class NCFixerMissionSystem
{
    #region Recon

    private bool TryStartRecon(ActiveMission active, NCFixerMissionPrototype mission, EntityUid taker)
    {
        var candidates = new List<EntityUid>();
        var query = EntityQueryEnumerator<NCMissionMarkerComponent>();
        while (query.MoveNext(out var uid, out var marker))
        {
            if (marker.Kind == NCMissionMarkerKind.Objective && (mission.Owner == null || marker.Owner == mission.Owner))
                candidates.Add(uid);
        }

        if (candidates.Count == 0)
            return false;

        _random.Shuffle(candidates);
        for (var i = 0; i < Math.Min(Math.Max(1, mission.ReconPoints), candidates.Count); i++)
        {
            var point = candidates[i];
            active.ReconTargets.Add(point);
            if (mission.Encounter is { } encounter)
                SpawnEncounter(active, encounter, point, taker);
        }

        active.Objective = active.ReconTargets[0];
        return true;
    }

    /// <summary>A point counts as scouted once anyone with a mind stands close to it.</summary>
    private void CheckRecon(ActiveMission active, NCFixerMissionPrototype mission)
    {
        foreach (var point in active.ReconTargets)
        {
            if (active.ReconVisited.Contains(point) || TerminatingOrDeleted(point))
                continue;

            foreach (var mind in _lookup.GetEntitiesInRange<MindContainerComponent>(Transform(point).Coordinates, ReconRange))
            {
                if (!mind.Comp.HasMind || HasComp<NCMissionGuardComponent>(mind))
                    continue;

                active.ReconVisited.Add(point);
                Notify(active, Loc.GetString("nc-fixer-recon-progress",
                    ("done", active.ReconVisited.Count), ("total", active.ReconTargets.Count)));
                break;
            }
        }

        if (active.ReconVisited.Count >= active.ReconTargets.Count)
            Complete(active, mission);
    }

    /// <summary>Whether any defender of the mission has turned on someone.</summary>
    private bool AnyGuardHostile(ActiveMission active)
    {
        foreach (var uid in active.Spawned)
        {
            if (TryComp<NCMissionGuardComponent>(uid, out var guard) && guard.Hostile.Count > 0)
                return true;
        }

        return false;
    }

    #endregion

    #region Laundering

    private bool TryStartLaundering(ActiveMission active, NCFixerMissionPrototype mission)
    {
        if (mission.LaunderAccount is not { } shellId ||
            !_bank.TryGetDepartmentAccount(shellId, out var shell) ||
            !_bank.TryGetProfileAccount(active.ProfileId, out var account) ||
            !_bank.TryCreditAnonymous(account, mission.LaunderAmount, mission.ID))
        {
            return false;
        }

        active.LaunderTarget = shell.Number;
        active.TargetName = Loc.GetString("nc-fixer-launder-target", ("amount", mission.LaunderAmount), ("account", shell.Number));
        Notify(active, Loc.GetString("nc-fixer-launder-credited", ("amount", mission.LaunderAmount), ("account", shell.Number)));
        return true;
    }

    private void OnBankTransferred(ref NCBankTransferredEvent args)
    {
        var target = args.Target.Number;
        var payer = args.Payer.ProfileId;
        var amount = args.Amount;
        foreach (var active in _active.Values.ToList())
        {
            if (active.Status != NCFixerMissionStatus.Active || active.LaunderTarget != target || active.ProfileId != payer ||
                !_prototypes.TryIndex(active.Mission, out var mission))
            {
                continue;
            }

            active.Laundered += amount;
            if (active.Laundered >= mission.LaunderAmount)
                Complete(active, mission);
            else
                Notify(active, Loc.GetString("nc-fixer-launder-progress",
                    ("done", active.Laundered), ("total", mission.LaunderAmount)));
        }
    }

    /// <summary>A failed laundering job: the fixer's people take back what never reached the shell company.</summary>
    private void TakeBackLaundering(ActiveMission active, NCFixerMissionPrototype mission)
    {
        var owed = mission.LaunderAmount - active.Laundered;
        if (owed <= 0 || !_bank.TryGetProfileAccount(active.ProfileId, out var account))
            return;

        var taken = _bank.TakeBack(account, owed, mission.ID);
        Notify(active, Loc.GetString("nc-fixer-launder-taken", ("amount", taken)));
    }

    #endregion
}
