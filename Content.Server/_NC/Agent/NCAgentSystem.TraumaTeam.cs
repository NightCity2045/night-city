// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Personnel;
using Content.Server._NC.TraumaTeam;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.Shards.TraumaResponse;
using Content.Shared._NC.Agent.TraumaTeam;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.TraumaTeam;
using Content.Shared.PDA;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    private static readonly ProtoId<DepartmentPrototype> TraumaTeamDepartment = "TraumaTeam";
    private static readonly ProtoId<JobPrototype> TraumaTeamParamedic = "TraumaTeamParamedic";
    private static readonly ProtoId<JobPrototype> TraumaTeamOperative = "TraumaTeamOperative";
    private static readonly HashSet<ProtoId<JobPrototype>> TraumaTeamPublicJobs =
        [TraumaTeamParamedic, TraumaTeamOperative];

    [Dependency] private NCTraumaTeamSubscriptionSystem _traumaSubscriptions = default!;
    [Dependency] private NCPersonnelConsoleSystem _personnel = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCActiveWorldTimeSystem _activeWorldTime = default!;
    [Dependency] private NCTraumaResponseSystem _traumaResponse = default!;

    private void InitializeTraumaTeam()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentTraumaTeamHireMessage>(OnTraumaTeamHire);
        SubscribeLocalEvent<NCAgentComponent, NCAgentTraumaTeamPurchaseMessage>(OnTraumaTeamPurchase);
        SubscribeLocalEvent<NCAgentComponent, NCAgentTraumaTeamRefreshMessage>(OnTraumaTeamRefresh);
        SubscribeLocalEvent<NCAgentComponent, NCAgentTraumaTeamRequestResponseMessage>(OnTraumaTeamRequestResponse);
        SubscribeLocalEvent<NCAgentComponent, NCAgentTraumaTeamCancelResponseMessage>(OnTraumaTeamCancelResponse);
    }

    private async void OnTraumaTeamHire(EntityUid uid, NCAgentComponent component, NCAgentTraumaTeamHireMessage args)
    {
        if (!CanUseTraumaTeam(uid, args.Actor, out var owner) ||
            !_prototypes.TryIndex<JobPrototype>(args.JobId, out var job) ||
            !TraumaTeamPublicJobs.Contains(job.ID) || !_processing.Add((uid, args.Actor)))
            return;
        try
        {
            var result = await _personnel.TrySelfHireAsync(args.Actor, owner, TraumaTeamDepartment, job.ID,
                TraumaTeamPublicJobs);
            await SendTraumaTeamSnapshotAsync(uid, args.Actor, HireNotice(result));
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, args.Actor, "submit Trauma Team employment", exception);
        }
        finally
        {
            _processing.Remove((uid, args.Actor));
        }
    }

    private async void OnTraumaTeamPurchase(EntityUid uid, NCAgentComponent component,
        NCAgentTraumaTeamPurchaseMessage args)
    {
        if (!CanUseTraumaTeam(uid, args.Actor, out var owner) || !_processing.Add((uid, args.Actor)))
            return;
        try
        {
            var result = await _traumaSubscriptions.TryPurchaseAsync(owner, args.PlanId, args.Days, args.CommandId);
            await SendTraumaTeamSnapshotAsync(uid, args.Actor, PurchaseNotice(result.Status));
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, args.Actor, "purchase Trauma Team coverage", exception);
        }
        finally
        {
            _processing.Remove((uid, args.Actor));
        }
    }

    private async void OnTraumaTeamRefresh(EntityUid uid, NCAgentComponent component,
        NCAgentTraumaTeamRefreshMessage args)
    {
        await SendTraumaTeamSnapshotAsync(uid, args.Actor);
    }

    private async void OnTraumaTeamRequestResponse(EntityUid uid, NCAgentComponent component,
        NCAgentTraumaTeamRequestResponseMessage args)
    {
        if (!CanUseTraumaTeam(uid, args.Actor, out var owner) || !_processing.Add((uid, args.Actor)))
            return;
        try
        {
            var result = await _traumaResponse.TryRequestAsync(args.Actor, owner, args.Urgency, args.Description);
            await SendTraumaTeamSnapshotAsync(uid, args.Actor, RequestNotice(result.Status));
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, args.Actor, "request Trauma Team response", exception);
        }
        finally
        {
            _processing.Remove((uid, args.Actor));
        }
    }

    private async void OnTraumaTeamCancelResponse(EntityUid uid, NCAgentComponent component,
        NCAgentTraumaTeamCancelResponseMessage args)
    {
        if (!CanUseTraumaTeam(uid, args.Actor, out var owner))
            return;
        var success = _traumaResponse.TryCancelClientCall(owner, args.CallId, args.ExpectedVersion);
        await SendTraumaTeamSnapshotAsync(uid, args.Actor,
            success ? "nc-trauma-team-response-cancelled" : "nc-trauma-team-operation-failed");
    }

    private bool CanUseTraumaTeam(EntityUid uid, EntityUid user, out Content.Shared._NC.Identity.NCCharacterId owner)
    {
        owner = default;
        return TryGetView(uid, user, out _, out var view) && view.ActiveApp == NCAgentApp.TraumaTeam &&
            TryGetCharacterId(user, out owner) && owner == view.Owner;
    }

    private async Task SendTraumaTeamSnapshotAsync(EntityUid uid, EntityUid user, string notice = "")
    {
        if (!CanUseTraumaTeam(uid, user, out var owner) ||
            !_prototypes.TryIndex(TraumaTeamDepartment, out DepartmentPrototype? department))
            return;

        var currentJob = await _database.GetNCActiveJobAsync(owner);
        var roster = await _database.GetNCPersonnelRosterAsync(TraumaTeamPublicJobs.Select(value => value.Id).ToArray());
        var occupied = roster.GroupBy(value => value.JobPrototypeId)
            .ToDictionary(value => value.Key, value => value.Count());
        var jobs = new List<NCAgentTraumaTeamJobSummary>();
        foreach (var jobId in TraumaTeamPublicJobs)
        {
            var job = _prototypes.Index(jobId);
            var limit = department.NCPositionLimits.GetValueOrDefault(jobId);
            var count = occupied.GetValueOrDefault(jobId.Id);
            jobs.Add(new(job.ID, job.LocalizedName, job.LocalizedDescription ?? string.Empty, job.Salary, count, limit,
                currentJob == null && count < limit));
        }

        var plans = _prototypes.EnumeratePrototypes<NCTraumaTeamSubscriptionPlanPrototype>()
            .OrderBy(value => value.DispatchPriority)
            .Select(value => new NCAgentTraumaTeamPlanSummary(value.ID, Loc.GetString(value.Name),
                Loc.GetString(value.Description), value.PricePerDay, value.DispatchPriority,
                value.TreatmentCoverageBasisPoints, value.MaximumThreatBasisPoints, value.Color))
            .ToList();
        var account = await _finance.GetPrimaryAccountAsync(NCPartyRef.ForCharacter(owner), true);
        var subscription = await _traumaSubscriptions.GetAsync(owner, true);
        var responseCall = _traumaResponse.GetClientCall(owner);
        long remaining = 0;
        string? activePlan = null;
        if (_activeWorldTime.TryGetCurrentTime(out _, out var now) && subscription?.Snapshot.ExpiresAt > now)
        {
            activePlan = subscription.Snapshot.PlanId;
            remaining = (long) Math.Ceiling(subscription.Snapshot.ExpiresAt.ElapsedSince(now).TotalSeconds);
        }

        if (CanUseTraumaTeam(uid, user, out var currentOwner) && currentOwner == owner)
        {
            _ui.ServerSendUiMessage(uid, PdaUiKey.Key, new NCAgentTraumaTeamUpdateMessage(
                currentJob, jobs, plans, account?.Snapshot.Balance ?? 0, activePlan, remaining,
                NCTraumaTeamSubscriptionSystem.MinimumDays, NCTraumaTeamSubscriptionSystem.MaximumDaysPerPurchase,
                responseCall, notice), user);
        }
    }

    private static string HireNotice(NCPersonnelSelfHireStatus status) => status switch
    {
        NCPersonnelSelfHireStatus.Success => "nc-trauma-team-hire-success",
        NCPersonnelSelfHireStatus.AlreadyEmployed => "nc-trauma-team-hire-employed",
        NCPersonnelSelfHireStatus.PositionFull => "nc-trauma-team-hire-full",
        NCPersonnelSelfHireStatus.Banned => "nc-trauma-team-hire-banned",
        _ => "nc-trauma-team-operation-failed",
    };

    private static string PurchaseNotice(NCTraumaTeamPurchaseStatus status) => status switch
    {
        NCTraumaTeamPurchaseStatus.Applied => "nc-trauma-team-purchase-success",
        NCTraumaTeamPurchaseStatus.Duplicate => "nc-trauma-team-purchase-success",
        NCTraumaTeamPurchaseStatus.PlanLocked => "nc-trauma-team-plan-locked",
        NCTraumaTeamPurchaseStatus.InsufficientFunds => "nc-trauma-team-insufficient-funds",
        NCTraumaTeamPurchaseStatus.ClockUnavailable => "nc-trauma-team-clock-unavailable",
        _ => "nc-trauma-team-operation-failed",
    };

    private static string RequestNotice(NCTraumaResponseRequestStatus status) => status switch
    {
        NCTraumaResponseRequestStatus.Success => "nc-trauma-team-response-requested",
        NCTraumaResponseRequestStatus.NoCoverage => "nc-trauma-team-response-no-coverage",
        NCTraumaResponseRequestStatus.AlreadyActive => "nc-trauma-team-response-already-active",
        NCTraumaResponseRequestStatus.Cooldown => "nc-trauma-team-response-cooldown",
        NCTraumaResponseRequestStatus.LocationUnavailable => "nc-trauma-team-response-location-failed",
        _ => "nc-trauma-team-operation-failed",
    };
}
