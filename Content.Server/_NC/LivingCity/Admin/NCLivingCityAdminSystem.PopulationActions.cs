// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Population;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.Population;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// F7 population operations. Validation is read-only; import and projection rebuild remain explicit, confirmed and
/// audited round-local administrative mutations.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private async void OnPopulationActionRequest(
        NCLivingCityAdminPopulationActionRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;

        var validationOnly = request.Action == NCLivingCityAdminPopulationAction.ValidateProfile;
        if (validationOnly ? !CanRead(args) : !CanMutate(args))
        {
            SendPopulationResult(args, request, NCLivingCityAdminMutationStatus.Unauthorized, Guid.Empty, [],
                "not-authorized");
            return;
        }
        if (!ValidPopulationActionRequest(request, validationOnly))
        {
            SendPopulationResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "invalid-population-action-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendPopulationResult(args, request, NCLivingCityAdminMutationStatus.RateLimited, Guid.Empty, [],
                "rate-limited");
            return;
        }

        if (validationOnly)
        {
            var validation = ValidatePopulationProfile(request.ProfileId);
            SendPopulationResult(args, request, validation.Status, Guid.Empty, validation.Fields, validation.Error);
            return;
        }

        var fingerprint = PopulationActionFingerprint(request);
        if (request.ConfirmationToken == Guid.Empty)
        {
            var token = IssueConfirmation(args.SenderSession.UserId, fingerprint);
            SendPopulationResult(args, request, NCLivingCityAdminMutationStatus.ConfirmationRequired, token, [], null);
            return;
        }
        if (!ConsumeConfirmation(args.SenderSession.UserId, request.ConfirmationToken, fingerprint))
        {
            SendPopulationResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "confirmation-missing-expired-or-does-not-match");
            return;
        }

        PopulationActionExecution execution;
        try
        {
            execution = await ExecutePopulationActionAsync(request, args.SenderSession.UserId);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 population action {request.Action} failed: {exception}");
            execution = new PopulationActionExecution(NCLivingCityAdminMutationStatus.Failed, [],
                "storage-or-validation-failure");
        }

        AddAdminAudit(args, $"population.{request.Action}", PopulationAuditTarget(request), request.Reason,
            execution.Status.ToString());
        SendPopulationResult(args, request, execution.Status, Guid.Empty, execution.Fields, execution.Error);
    }

    private Task<PopulationActionExecution> ExecutePopulationActionAsync(
        NCLivingCityAdminPopulationActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        return request.Action switch
        {
            NCLivingCityAdminPopulationAction.ImportProfile => ImportPopulationProfileAsync(request, administrator),
            NCLivingCityAdminPopulationAction.RebuildZoneStates =>
                Task.FromResult(RebuildZoneStateProjections()),
            NCLivingCityAdminPopulationAction.AllocateResidences =>
                AllocateResidencesAsync(request, administrator),
            _ => Task.FromResult(new PopulationActionExecution(
                NCLivingCityAdminMutationStatus.Rejected, [], "unsupported-population-action")),
        };
    }

    private PopulationActionExecution ValidatePopulationProfile(string profileId)
    {
        var fields = new FieldBuilder();
        var errors = new List<string>();
        var profile = new ProtoId<NCCityPopulationProfilePrototype>(profileId.Trim());
        var valid = _population.ValidateProfile(profile, errors);
        fields.Add("validation", "profile", profile.Id);
        fields.Add("validation", "valid", valid);
        fields.Add("validation", "error-count", errors.Count);
        foreach (var error in errors.Take(64))
            fields.Add("validation-errors", "error", error);
        return new PopulationActionExecution(
            valid ? NCLivingCityAdminMutationStatus.Applied : NCLivingCityAdminMutationStatus.Rejected,
            fields.Build(),
            valid ? null : "population-profile-invalid");
    }

    private async Task<PopulationActionExecution> ImportPopulationProfileAsync(
        NCLivingCityAdminPopulationActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        var report = await _population.ImportAsync(
            new ProtoId<NCCityPopulationProfilePrototype>(request.ProfileId.Trim()),
            AdminAuditSource(administrator, request.Reason));
        var fields = new FieldBuilder();
        fields.Add("report", "status", report.Status);
        fields.Add("report", "profile", report.ProfileId);
        fields.Add("report", "requested", report.Requested);
        fields.Add("report", "created", report.Created);
        fields.Add("report", "existing", report.Existing);
        fields.Add("report", "conflicts", report.Conflicts);
        fields.Add("report", "failed", report.Failed);
        foreach (var failure in report.Failures.Take(20))
            fields.Add("failures", failure.StableLocalId, failure.Error);
        if (report.Failures.Count > 20)
            fields.Add("failures", "omitted", report.Failures.Count - 20);

        var importComplete = report.Status == NCCityPopulationImportStatus.Completed &&
                             report.Conflicts == 0 && report.Failed == 0;
        NCCityResidenceAllocationReport? residences = null;
        if (importComplete)
        {
            // Population import is the canonical bootstrap path, so old residents receive coordinate-free homes in
            // this same audited operation instead of requiring a hidden second setup step.
            residences = await _residences.AllocateAsync(AdminAuditSource(administrator, request.Reason));
            fields.Add("residences", "status", residences.Status);
            fields.Add("residences", "profiles", residences.ResidenceProfiles);
            fields.Add("residences", "existing", residences.ExistingResidences);
            fields.Add("residences", "assigned", residences.AssignedResidences);
            fields.Add("residences", "unassigned", residences.UnassignedResidences);
            fields.Add("residences", "failed-mutations", residences.FailedMutations);
        }

        var success = importComplete && residences?.Status == NCCityResidenceAllocationStatus.Completed;
        return new PopulationActionExecution(
            success ? NCLivingCityAdminMutationStatus.Applied : NCLivingCityAdminMutationStatus.Rejected,
            fields.Build(),
            success ? null : residences?.Error ?? "population-import-not-complete");
    }

    private PopulationActionExecution RebuildZoneStateProjections()
    {
        var fields = new FieldBuilder();
        var started = _zoneStates.ForceRebuild();
        fields.Add("rebuild", "started", started);
        fields.Add("rebuild", "initial-load-complete", _zoneStates.InitialLoadComplete);
        fields.Add("rebuild", "indexed-actors", _zoneStates.IndexedActorCount);
        fields.Add("rebuild", "pending-scopes", _zoneStates.PendingScopeCount);
        return new PopulationActionExecution(
            started ? NCLivingCityAdminMutationStatus.Applied : NCLivingCityAdminMutationStatus.Rejected,
            fields.Build(),
            started ? null : "active-world-round-required");
    }

    private async Task<PopulationActionExecution> AllocateResidencesAsync(
        NCLivingCityAdminPopulationActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        var report = await _residences.AllocateAsync(AdminAuditSource(administrator, request.Reason));
        var fields = new FieldBuilder();
        fields.Add("residences", "status", report.Status);
        fields.Add("residences", "scanned-actors", report.ScannedActors);
        fields.Add("residences", "profiles", report.ResidenceProfiles);
        fields.Add("residences", "existing", report.ExistingResidences);
        fields.Add("residences", "assigned", report.AssignedResidences);
        fields.Add("residences", "abstract-home", report.AbstractHomeActors);
        fields.Add("residences", "unassigned", report.UnassignedResidences);
        fields.Add("residences", "failed-mutations", report.FailedMutations);
        var success = report.Status == NCCityResidenceAllocationStatus.Completed;
        return new PopulationActionExecution(
            success ? NCLivingCityAdminMutationStatus.Applied : NCLivingCityAdminMutationStatus.Rejected,
            fields.Build(),
            success ? null : report.Error ?? "residence-allocation-not-complete");
    }

    private static bool ValidPopulationActionRequest(
        NCLivingCityAdminPopulationActionRequest request,
        bool validationOnly)
    {
        if (!Enum.IsDefined(request.Action) || !ValidInput(request.ProfileId, 128) ||
            !ValidInput(request.Reason, 256))
        {
            return false;
        }
        if ((request.Action == NCLivingCityAdminPopulationAction.ValidateProfile ||
             request.Action == NCLivingCityAdminPopulationAction.ImportProfile) &&
            string.IsNullOrWhiteSpace(request.ProfileId))
        {
            return false;
        }
        return validationOnly || !string.IsNullOrWhiteSpace(request.Reason);
    }

    private static string PopulationActionFingerprint(NCLivingCityAdminPopulationActionRequest request) =>
        Fingerprint(
            ((byte) request.Action).ToString(),
            request.ProfileId.Trim(),
            request.Reason.Trim());

    private static string PopulationAuditTarget(NCLivingCityAdminPopulationActionRequest request) =>
        request.Action switch
        {
            NCLivingCityAdminPopulationAction.RebuildZoneStates => "all-zone-state-projections",
            NCLivingCityAdminPopulationAction.AllocateResidences => "all-resident-homes",
            _ => $"profile={request.ProfileId.Trim()}",
        };

    private void SendPopulationResult(
        EntitySessionEventArgs args,
        NCLivingCityAdminPopulationActionRequest request,
        NCLivingCityAdminMutationStatus status,
        Guid confirmationToken,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(SuccessfulResponse(status), error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminPopulationActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }

    private sealed record PopulationActionExecution(
        NCLivingCityAdminMutationStatus Status,
        NCLivingCityAdminField[] Fields,
        string? Error);
}
