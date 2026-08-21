// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Admin;

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
            if (!TryGuid(request.TargetId, out var messageId))
            {
                error = "invalid-outbox-message-id";
            }
            else if (!await _database.RequeueNCOutboxMessageAsync(
                         messageId,
                         DateTime.UtcNow,
                         request.ResetAttempts))
            {
                error = "outbox-message-not-found-processed-or-concurrently-changed";
            }
            else
            {
                _outbox.Wake();
                status = NCLivingCityAdminMutationStatus.Applied;
                fields.Add("outbox-requeue", "message-id", messageId);
                fields.Add("outbox-requeue", "reset-attempts", request.ResetAttempts);
                fields.Add("outbox-requeue", "dispatcher-active", _outbox.IsActive);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 infrastructure action {request.Action} failed: {exception}");
            status = NCLivingCityAdminMutationStatus.Failed;
            error = "storage-or-validation-failure";
        }

        AddAdminAudit(args, $"infrastructure.{request.Action}", $"outbox={request.TargetId.Trim()}",
            request.Reason, status.ToString());
        SendInfrastructureResult(args, request, status, Guid.Empty, fields.Build(), error);
    }

    private static bool ValidInfrastructureActionRequest(NCLivingCityAdminInfrastructureActionRequest request) =>
        Enum.IsDefined(request.Action) && ValidInput(request.TargetId, 64) &&
        ValidInput(request.Reason, 256) && !string.IsNullOrWhiteSpace(request.TargetId) &&
        !string.IsNullOrWhiteSpace(request.Reason);

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
        RaiseNetworkEvent(new NCLivingCityAdminInfrastructureActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }
}
