// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.LivingCity.Police.NCPD;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Turns a factually lost, already reported suspect into a canonical wanted record. It never derives a suspect from
/// faction membership or neural text.
/// </summary>
public sealed partial class NCNcpdWantedSystem : EntitySystem
{
    private const string ActionId = "ncpd-create-wanted-record";

    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCPoliceWantedRegistrySystem _wanted = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignments = default!;

    private readonly Dictionary<Guid, PendingWanted> _pending = [];

    public int PendingMutations => _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _pending.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _pending.Clear());
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (args.Step.ActionId != ActionId || args.EntityUid is not { } officer || !Exists(officer) ||
            !HasComp<NCNcpdOfficerComponent>(officer))
        {
            return;
        }

        args.Handled = true;
        _assignments.YieldToProfessionalAction(officer);
        if (!_clock.TryGetCurrentTime(out _, out var now))
        {
            args.Failure = "ncpd-wanted-clock-unavailable";
            return;
        }
        if (!TryComp<NCNcpdSuspectClaimComponent>(officer, out var claim) ||
            claim.Status != NCNcpdSuspectStatus.Lost)
        {
            args.Completed = true;
            return;
        }
        if (!claim.IncidentId.IsValid || claim.SourceWorldEventId == Guid.Empty || !claim.SuspectParty.IsValid ||
            !_parties.TryResolve(officer, out var officerParty))
        {
            args.Failure = "ncpd-wanted-legal-basis-unavailable";
            return;
        }

        if (!_pending.TryGetValue(args.InstanceId, out var pending))
        {
            var projection = EnsureComp<NCNcpdWantedProjectionComponent>(officer);
            projection.IncidentId = claim.IncidentId;
            projection.Subject = claim.SuspectParty;
            projection.Submitting = true;
            projection.UpdatedAt = now;
            projection.Failure = string.Empty;
            pending = new PendingWanted(officer, CreateAsync(
                claim.IncidentId,
                claim.SourceWorldEventId,
                claim.SuspectParty,
                claim.Basis,
                officerParty));
            _pending.Add(args.InstanceId, pending);
        }

        if (!pending.Operation.IsCompleted)
            return;
        _pending.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        var current = EnsureComp<NCNcpdWantedProjectionComponent>(officer);
        current.Submitting = false;
        current.UpdatedAt = now;
        current.Failure = result.Error ?? string.Empty;
        if (!result.Success || result.Record == null)
        {
            args.Failure = result.Error ?? "ncpd-wanted-persistence-rejected";
            return;
        }

        current.WantedId = result.Record.Snapshot.WantedId;
        current.Status = result.Record.Snapshot.Status;
        claim.ResultReason = "suspect-wanted-record-created";
        claim.UpdatedAt = now;
        args.Completed = true;
    }

    private async Task<WantedActionResult> CreateAsync(
        Content.Shared._NC.LivingCity.CityServices.NCCityIncidentId incidentId,
        Guid sourceWorldEventId,
        Content.Shared._NC.Persistence.Identity.NCPartyRef suspect,
        string basis,
        Content.Shared._NC.Persistence.Identity.NCPartyRef officer)
    {
        try
        {
            var incident = await _incidents.GetAsync(incidentId, true);
            if (incident == null || incident.Snapshot.Causes.All(value =>
                    value.SourceWorldEventId != sourceWorldEventId) ||
                incident.Snapshot.Participants.All(value =>
                    value.Party != suspect || value.Role !=
                    Content.Shared._NC.LivingCity.CityServices.NCIncidentParticipantRole.Suspect))
            {
                return new(false, null, "ncpd-wanted-incident-basis-rejected");
            }

            var mutation = await _wanted.CreateAsync(
                suspect,
                incidentId,
                sourceWorldEventId,
                incident.Snapshot.Severity,
                basis,
                officer,
                "ncpd-lost-suspect");
            return new(
                mutation.Status is NCPoliceWantedMutationStatus.Applied or NCPoliceWantedMutationStatus.Duplicate,
                mutation.Record,
                mutation.Error);
        }
        catch (Exception exception)
        {
            return new(false, null, $"ncpd-wanted-exception:{exception.GetType().Name}");
        }
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (args.ActionId == ActionId)
            _pending.Remove(args.InstanceId);
    }

    private readonly record struct PendingWanted(EntityUid Officer, Task<WantedActionResult> Operation);
    private readonly record struct WantedActionResult(bool Success, NCPoliceWantedRecord? Record, string? Error);
}
