// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Admin;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Audited runtime geography corrections. Authored YAML and mapper drafts are deliberately outside this boundary.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private void OnGeographyActionRequest(
        NCLivingCityAdminGeographyActionRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanMutate(args))
        {
            SendGeographyResult(args, request, NCLivingCityAdminMutationStatus.Unauthorized, Guid.Empty, [],
                "not-authorized");
            return;
        }
        if (!ValidGeographyActionRequest(request))
        {
            SendGeographyResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "invalid-geography-action-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendGeographyResult(args, request, NCLivingCityAdminMutationStatus.RateLimited, Guid.Empty, [],
                "rate-limited");
            return;
        }

        var fingerprint = GeographyActionFingerprint(request);
        if (request.ConfirmationToken == Guid.Empty)
        {
            var token = IssueConfirmation(args.SenderSession.UserId, fingerprint);
            SendGeographyResult(args, request, NCLivingCityAdminMutationStatus.ConfirmationRequired, token, [], null);
            return;
        }
        if (!ConsumeConfirmation(args.SenderSession.UserId, request.ConfirmationToken, fingerprint))
        {
            SendGeographyResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "confirmation-missing-expired-or-does-not-match");
            return;
        }

        var fields = new FieldBuilder();
        var status = NCLivingCityAdminMutationStatus.Rejected;
        string? error = null;
        if (!NCZoneId.TryParse(request.ZoneId.Trim(), out var zoneId))
        {
            error = "invalid-city-zone-id";
        }
        else if (!Enum.TryParse<NCZoneActivityMode>(request.ActivityMode.Trim(), true, out var requestedMode) ||
                 !Enum.IsDefined(requestedMode))
        {
            error = "invalid-zone-activity-mode";
        }
        else if (!_zones.TryGetActivityMode(zoneId, out var previousMode))
        {
            error = "zone-does-not-support-activity-mode";
        }
        else if (previousMode == requestedMode)
        {
            status = NCLivingCityAdminMutationStatus.Duplicate;
            fields.Add("zone-activity", "zone-id", zoneId);
            fields.Add("zone-activity", "previous", previousMode);
            fields.Add("zone-activity", "current", requestedMode);
        }
        else if (!_zones.SetActivityMode(zoneId, requestedMode))
        {
            error = "zone-activity-concurrently-changed";
        }
        else
        {
            status = NCLivingCityAdminMutationStatus.Applied;
            fields.Add("zone-activity", "zone-id", zoneId);
            fields.Add("zone-activity", "previous", previousMode);
            fields.Add("zone-activity", "current", requestedMode);
        }

        AddAdminAudit(args, $"geography.{request.Action}", $"zone={request.ZoneId.Trim()}",
            request.Reason, status.ToString());
        SendGeographyResult(args, request, status, Guid.Empty, fields.Build(), error);
    }

    private static bool ValidGeographyActionRequest(NCLivingCityAdminGeographyActionRequest request) =>
        Enum.IsDefined(request.Action) && ValidInput(request.ZoneId, 64) && ValidInput(request.ActivityMode, 32) &&
        ValidInput(request.Reason, 256) && !string.IsNullOrWhiteSpace(request.ZoneId) &&
        !string.IsNullOrWhiteSpace(request.ActivityMode) && !string.IsNullOrWhiteSpace(request.Reason);

    private static string GeographyActionFingerprint(NCLivingCityAdminGeographyActionRequest request) => Fingerprint(
        ((byte) request.Action).ToString(),
        request.ZoneId.Trim(),
        request.ActivityMode.Trim(),
        request.Reason.Trim());

    private void SendGeographyResult(
        EntitySessionEventArgs args,
        NCLivingCityAdminGeographyActionRequest request,
        NCLivingCityAdminMutationStatus status,
        Guid confirmationToken,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        RaiseNetworkEvent(new NCLivingCityAdminGeographyActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }
}
