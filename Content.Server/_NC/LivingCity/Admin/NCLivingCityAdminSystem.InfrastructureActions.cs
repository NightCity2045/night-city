// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.City.Zones;
using Content.Server._NC.LivingCity.Economy;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Audited recovery operations for infrastructure owned by Living City. Payloads never cross this boundary.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private async void OnInfrastructureActionRequest(
        NCLivingCityAdminInfrastructureActionRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanMutate(args))
        {
            SendInfrastructureResult(args, request, NCLivingCityAdminMutationStatus.Unauthorized, Guid.Empty, [],
                "not-authorized");
            return;
        }
        if (!ValidInfrastructureActionRequest(request))
        {
            SendInfrastructureResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "invalid-infrastructure-action-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendInfrastructureResult(args, request, NCLivingCityAdminMutationStatus.RateLimited, Guid.Empty, [],
                "rate-limited");
            return;
        }

        var fingerprint = InfrastructureActionFingerprint(request);
        if (request.ConfirmationToken == Guid.Empty)
        {
            var token = IssueConfirmation(args.SenderSession.UserId, fingerprint);
            SendInfrastructureResult(args, request, NCLivingCityAdminMutationStatus.ConfirmationRequired, token, [],
                null);
            return;
        }
        if (!ConsumeConfirmation(args.SenderSession.UserId, request.ConfirmationToken, fingerprint))
        {
            SendInfrastructureResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "confirmation-missing-expired-or-does-not-match");
            return;
        }

        var fields = new FieldBuilder();
        var status = NCLivingCityAdminMutationStatus.Rejected;
        string? error = null;
        try
        {
            switch (request.Action)
            {
                case NCLivingCityAdminInfrastructureAction.RequeueOutboxMessage:
                    if (!TryGuid(request.TargetId, out var messageId))
                    {
                        error = "invalid-outbox-message-id";
                        break;
                    }
                    if (!await _database.RequeueNCOutboxMessageAsync(
                            messageId, DateTime.UtcNow, request.ResetAttempts))
                    {
                        error = "outbox-message-not-found-processed-or-concurrently-changed";
                        break;
                    }
                    _outbox.Wake();
                    status = NCLivingCityAdminMutationStatus.Applied;
                    fields.Add("outbox-requeue", "message-id", messageId);
                    fields.Add("outbox-requeue", "reset-attempts", request.ResetAttempts);
                    fields.Add("outbox-requeue", "dispatcher-active", _outbox.IsActive);
                    break;
                case NCLivingCityAdminInfrastructureAction.ReconcileEconomyPage:
                    var report = await _economicReconciliation.AuditAsync(
                        new NCEconomicReconciliationCursor(null, null, null));
                    status = NCLivingCityAdminMutationStatus.Applied;
                    fields.Add("economic-reconciliation", "stock-nodes", report.StockNodesScanned);
                    fields.Add("economic-reconciliation", "commodity-batches", report.CommodityBatchesScanned);
                    fields.Add("economic-reconciliation", "items", report.ItemsScanned);
                    fields.Add("economic-reconciliation", "reference-checks", report.ReferenceChecks);
                    fields.Add("economic-reconciliation", "issues", report.Issues.Count);
                    fields.Add("economic-reconciliation", "has-more", report.HasMore);
                    foreach (var issue in report.Issues.Take(16))
                    {
                        fields.Add("economic-issue", $"{issue.AggregateType}:{issue.AggregateId}",
                            $"{issue.Code}; related={issue.RelatedId}");
                    }
                    break;
                case NCLivingCityAdminInfrastructureAction.RebuildDistrictSafety:
                    status = NCLivingCityAdminMutationStatus.Applied;
                    fields.Add("district-safety", "rebuilt", _districtSafety.RebuildAll());
                    fields.Add("district-safety", "projections", _districtSafety.GetAll().Count);
                    break;
                case NCLivingCityAdminInfrastructureAction.RecoverServiceDispatch:
                    if (!TryGuid(request.TargetId, out var dispatchRaw))
                    {
                        error = "invalid-service-dispatch-id";
                        break;
                    }
                    var dispatch = await _cityServices.RecoverDispatchAsync(new NCServiceDispatchId(dispatchRaw));
                    if (dispatch == null)
                    {
                        error = "service-dispatch-not-found-or-round-inactive";
                        break;
                    }
                    status = NCLivingCityAdminMutationStatus.Applied;
                    fields.Add("service-dispatch-recovery", "dispatch-id", dispatch.Snapshot.DispatchId);
                    fields.Add("service-dispatch-recovery", "status", dispatch.Snapshot.Status);
                    fields.Add("service-dispatch-recovery", "service-id", dispatch.Snapshot.ServiceId);
                    fields.Add("service-dispatch-recovery", "due-at",
                        dispatch.Snapshot.ExpectedArrivalAt?.Elapsed.ToString() ?? "none");
                    break;
                case NCLivingCityAdminInfrastructureAction.ReloadProperty:
                    if (!NCApartmentId.TryParse(request.TargetId.Trim(), out var propertyId))
                    {
                        error = "invalid-property-id";
                        break;
                    }
                    var property = await _properties.GetAsync(propertyId, true);
                    if (property == null)
                    {
                        error = "property-not-found";
                        break;
                    }
                    var contents = await _propertyContents.GetAsync(propertyId, true);
                    status = NCLivingCityAdminMutationStatus.Applied;
                    fields.Add("property-reload", "property-id", propertyId);
                    fields.Add("property-reload", "aggregate-version", property.AggregateVersion);
                    fields.Add("property-reload", "contents-present", contents?.Snapshot.Present ?? false);
                    fields.Add("property-reload", "contents-version", contents?.AggregateVersion.ToString() ?? "none");
                    break;
                case NCLivingCityAdminInfrastructureAction.ResetNeuralCircuit:
                    if (!_neural.ResetCircuitBreaker())
                    {
                        error = "neural-runtime-inactive";
                        break;
                    }
                    status = NCLivingCityAdminMutationStatus.Applied;
                    var neural = _neural.GetRuntimeSnapshot();
                    fields.Add("neural-recovery", "budget-mode", neural.BudgetMode);
                    fields.Add("neural-recovery", "consecutive-failures", neural.ConsecutiveFailures);
                    fields.Add("neural-recovery", "circuit-open-until", "none");
                    break;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 infrastructure action {request.Action} failed: {exception}");
            status = NCLivingCityAdminMutationStatus.Failed;
            error = "storage-or-validation-failure";
        }

        AddAdminAudit(args, $"infrastructure.{request.Action}", $"target={request.TargetId.Trim()}",
            request.Reason, status.ToString());
        SendInfrastructureResult(args, request, status, Guid.Empty, fields.Build(), error);
    }

    private static bool ValidInfrastructureActionRequest(NCLivingCityAdminInfrastructureActionRequest request) =>
        Enum.IsDefined(request.Action) && ValidInput(request.TargetId, 160) &&
        ValidInput(request.Reason, 256) && !string.IsNullOrWhiteSpace(request.Reason) &&
        (!InfrastructureTargetRequired(request.Action) || !string.IsNullOrWhiteSpace(request.TargetId));

    private static bool InfrastructureTargetRequired(NCLivingCityAdminInfrastructureAction action) => action is
        NCLivingCityAdminInfrastructureAction.RequeueOutboxMessage or
        NCLivingCityAdminInfrastructureAction.RecoverServiceDispatch or
        NCLivingCityAdminInfrastructureAction.ReloadProperty;

    private static string InfrastructureActionFingerprint(
        NCLivingCityAdminInfrastructureActionRequest request) => Fingerprint(
        ((byte) request.Action).ToString(),
        request.TargetId.Trim(),
        request.ResetAttempts.ToString(),
        request.Reason.Trim());

    private void SendInfrastructureResult(
        EntitySessionEventArgs args,
        NCLivingCityAdminInfrastructureActionRequest request,
        NCLivingCityAdminMutationStatus status,
        Guid confirmationToken,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(SuccessfulResponse(status), error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminInfrastructureActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }
}
