// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Admin;

public sealed partial class NCLivingCityAdminSystem
{
    private async void OnActorActionRequest(
        NCLivingCityAdminActorActionRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanMutate(args))
        {
            SendActorResult(args, request, NCLivingCityAdminMutationStatus.Unauthorized, Guid.Empty, [],
                "not-authorized");
            return;
        }
        if (!ValidActorActionRequest(request))
        {
            SendActorResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "invalid-actor-action-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendActorResult(args, request, NCLivingCityAdminMutationStatus.RateLimited, Guid.Empty, [],
                "rate-limited");
            return;
        }

        var fingerprint = ActorActionFingerprint(request);
        if (request.ConfirmationToken == Guid.Empty)
        {
            var token = IssueConfirmation(args.SenderSession.UserId, fingerprint);
            SendActorResult(args, request, NCLivingCityAdminMutationStatus.ConfirmationRequired, token, [], null);
            return;
        }
        if (!ConsumeConfirmation(args.SenderSession.UserId, request.ConfirmationToken, fingerprint))
        {
            SendActorResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "confirmation-missing-expired-or-does-not-match");
            return;
        }

        ActorActionExecution execution;
        try
        {
            execution = await ExecuteActorActionAsync(request, args.SenderSession.UserId);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 actor action {request.Action} failed: {exception}");
            execution = new ActorActionExecution(NCLivingCityAdminMutationStatus.Failed, [],
                "storage-or-validation-failure");
        }

        AddAdminAudit(args, $"actor.{request.Action}", ActorAuditTarget(request), request.Reason,
            execution.Status.ToString());
        SendActorResult(args, request, execution.Status, Guid.Empty, execution.Fields, execution.Error);
    }

    private async Task<ActorActionExecution> ExecuteActorActionAsync(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        return request.Action switch
        {
            NCLivingCityAdminActorAction.Create => await CreateActorAsync(request, administrator),
            NCLivingCityAdminActorAction.Freeze => await SetActorFrozenAsync(request, administrator, true),
            NCLivingCityAdminActorAction.Unfreeze => await SetActorFrozenAsync(request, administrator, false),
            NCLivingCityAdminActorAction.Move => await MoveActorAsync(request, administrator),
            NCLivingCityAdminActorAction.CaptureDomains => await CaptureActorDomainsAsync(request, administrator),
            NCLivingCityAdminActorAction.RestoreHealth => await RestoreActorHealthAsync(request),
            NCLivingCityAdminActorAction.Materialize => MaterializeActor(request, administrator),
            NCLivingCityAdminActorAction.Dematerialize => DematerializeActor(request, administrator),
            _ => new ActorActionExecution(NCLivingCityAdminMutationStatus.Rejected, [],
                "unsupported-actor-action"),
        };
    }

    private async Task<ActorActionExecution> CreateActorAsync(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        if (!Guid.TryParse(request.PopulationScopeId.Trim(), out var scope) || scope == Guid.Empty ||
            !_prototypes.HasIndex(new ProtoId<NCCityActorArchetypePrototype>(request.ArchetypeId.Trim())) ||
            !NCZoneId.TryParse(request.HomeZoneId.Trim(), out var home) ||
            !TryOptionalZone(request.WorkZoneId, out var work))
        {
            return RejectActor("invalid-create-actor-fields");
        }

        var result = await _actors.CreateAsync(new NCCityActorCreateRequest(
            request.RequestId,
            scope,
            request.StableLocalId.Trim(),
            request.FullName.Trim(),
            [],
            new ProtoId<NCCityActorArchetypePrototype>(request.ArchetypeId.Trim()),
            home,
            work,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason)));
        return ActorMutation(result);
    }

    private async Task<ActorActionExecution> SetActorFrozenAsync(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator,
        bool frozen)
    {
        if (!TryActor(request.ActorId, out var actorId))
            return RejectActor("invalid-actor-id");
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null)
            return RejectActor("actor-not-found");
        var result = await _actors.SetFrozenAsync(new NCCityActorFreezeMutationRequest(
            request.RequestId,
            actorId,
            actor.AggregateVersion,
            frozen,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason)));
        return ActorMutation(result);
    }

    private async Task<ActorActionExecution> MoveActorAsync(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        if (!TryActor(request.ActorId, out var actorId) ||
            !NCZoneId.TryParse(request.TargetZoneId.Trim(), out var zoneId) || !_zones.TryGetZone(zoneId, out _))
        {
            return RejectActor("invalid-actor-or-target-zone");
        }
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null || !_clock.TryGetCurrentTime(out _, out var now))
            return RejectActor(actor == null ? "actor-not-found" : "active-world-clock-not-ready");
        var location = actor.Snapshot.Location;
        var result = await _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
            request.RequestId,
            actorId,
            actor.AggregateVersion,
            zoneId,
            default,
            NCCityActorTravelPhase.Stationary,
            location.TravelMode,
            null,
            "admin-move",
            string.Empty,
            actor.Snapshot.Schedule.Availability,
            now,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason)));
        return ActorMutation(result);
    }

    private async Task<ActorActionExecution> CaptureActorDomainsAsync(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        if (!TryActor(request.ActorId, out var actorId))
            return RejectActor("invalid-actor-id");
        var health = await _actorDomains.GetHealthAsync(actorId, true);
        var inventory = await _actorDomains.GetInventoryAsync(actorId, true);
        if (health == null || inventory == null)
            return RejectActor("actor-domain-aggregates-not-found");
        var result = await _actorDomains.CaptureMaterializedAsync(new NCCityActorDomainCaptureRequest(
            request.RequestId,
            actorId,
            health.AggregateVersion,
            inventory.AggregateVersion,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason)));
        var fields = new FieldBuilder();
        fields.Add("result", "actor-id", actorId);
        fields.Add("result", "health-version", result.Health?.AggregateVersion);
        fields.Add("result", "inventory-version", result.Inventory?.AggregateVersion);
        return new ActorActionExecution(MapActorStatus(result.Status), fields.Build(), result.Error);
    }

    private async Task<ActorActionExecution> RestoreActorHealthAsync(NCLivingCityAdminActorActionRequest request)
    {
        if (!TryActor(request.ActorId, out var actorId))
            return RejectActor("invalid-actor-id");
        var health = await _actorDomains.GetHealthAsync(actorId, true);
        if (health == null || !_actorRuntime.TryGetEntity(actorId, out var entity) ||
            !_actorDomains.TryRestoreHealth(actorId, entity, health))
        {
            return RejectActor("actor-not-materialized-or-health-snapshot-incompatible");
        }
        var fields = new FieldBuilder();
        fields.Add("result", "actor-id", actorId);
        fields.Add("result", "health-version", health.AggregateVersion);
        return new ActorActionExecution(NCLivingCityAdminMutationStatus.Applied, fields.Build(), null);
    }

    private ActorActionExecution MaterializeActor(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        if (!TryActor(request.ActorId, out var actorId))
            return RejectActor("invalid-actor-id");
        var state = _materialization.Request(new NCCityMaterializationRequest(
            request.RequestId,
            actorId,
            NCCityMaterializationReason.Admin,
            request.Priority,
            unchecked((uint) actorId.Value.GetHashCode()),
            null,
            AdminAuditSource(administrator, request.Reason)));
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "actor-id", state.ActorId);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("request", "error", state.Error);
        return new ActorActionExecution(
            state.Status == NCCityMaterializationRequestStatus.Rejected
                ? NCLivingCityAdminMutationStatus.Rejected
                : NCLivingCityAdminMutationStatus.Applied,
            fields.Build(), state.Error);
    }

    private ActorActionExecution DematerializeActor(
        NCLivingCityAdminActorActionRequest request,
        Robust.Shared.Network.NetUserId administrator)
    {
        if (!TryActor(request.ActorId, out var actorId) || !_actorRuntime.TryGetEntity(actorId, out var entity))
            return RejectActor("actor-not-materialized");
        var state = _dematerialization.Request(new NCCityDematerializationRequest(
            request.RequestId,
            actorId,
            entity,
            NCCityDematerializationReason.Admin,
            request.Priority,
            AdminAuditSource(administrator, request.Reason)));
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "actor-id", state.ActorId);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("request", "error", state.Error);
        return new ActorActionExecution(
            state.Status == NCCityDematerializationStatus.Rejected
                ? NCLivingCityAdminMutationStatus.Rejected
                : NCLivingCityAdminMutationStatus.Applied,
            fields.Build(), state.Error);
    }

    private static ActorActionExecution ActorMutation(NCCityActorMutationResult result)
    {
        var fields = new FieldBuilder();
        fields.Add("result", "actor-id", result.ActorId);
        fields.Add("result", "aggregate-version", result.AggregateVersion);
        fields.Add("result", "domain-status", result.Status);
        return new ActorActionExecution(MapActorStatus(result.Status), fields.Build(), result.Error);
    }

    private static NCLivingCityAdminMutationStatus MapActorStatus(NCCityActorMutationStatus status) => status switch
    {
        NCCityActorMutationStatus.Applied => NCLivingCityAdminMutationStatus.Applied,
        NCCityActorMutationStatus.Duplicate => NCLivingCityAdminMutationStatus.Duplicate,
        NCCityActorMutationStatus.StorageFailure => NCLivingCityAdminMutationStatus.Failed,
        _ => NCLivingCityAdminMutationStatus.Rejected,
    };

    private static bool ValidActorActionRequest(NCLivingCityAdminActorActionRequest request)
    {
        if (!Enum.IsDefined(request.Action) || request.Priority is < 0 or > 1_000 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        {
            return false;
        }
        return ValidInput(request.ActorId, 64) && ValidInput(request.PopulationScopeId, 64) &&
               ValidInput(request.StableLocalId, 128) && ValidInput(request.ArchetypeId, 128) &&
               ValidInput(request.HomeZoneId, 160) && ValidInput(request.WorkZoneId, 160) &&
               ValidInput(request.FullName, 128) && ValidInput(request.TargetZoneId, 160);
    }

    private static bool ValidInput(string? value, int maximum) => value != null && value.Length <= maximum;

    private static string ActorActionFingerprint(NCLivingCityAdminActorActionRequest request) => Fingerprint(
        ((byte) request.Action).ToString(),
        request.ActorId.Trim(),
        request.PopulationScopeId.Trim(),
        request.StableLocalId.Trim(),
        request.ArchetypeId.Trim(),
        request.HomeZoneId.Trim(),
        request.WorkZoneId.Trim(),
        request.FullName.Trim(),
        request.TargetZoneId.Trim(),
        request.Priority.ToString(),
        request.Reason.Trim());

    private static string ActorAuditTarget(NCLivingCityAdminActorActionRequest request) =>
        request.Action == NCLivingCityAdminActorAction.Create
            ? $"scope={request.PopulationScopeId.Trim()}/local={request.StableLocalId.Trim()}"
            : $"actor={request.ActorId.Trim()}";

    private static bool TryActor(string value, out NCActorId actorId)
    {
        actorId = default;
        if (!Guid.TryParse(value.Trim(), out var raw) || raw == Guid.Empty)
            return false;
        actorId = new NCActorId(raw);
        return actorId.IsValid;
    }

    private static bool TryOptionalZone(string value, out NCZoneId zoneId)
    {
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            zoneId = default;
            return true;
        }
        return NCZoneId.TryParse(value.Trim(), out zoneId);
    }

    private static ActorActionExecution RejectActor(string error) =>
        new(NCLivingCityAdminMutationStatus.Rejected, [], error);

    private void SendActorResult(
        EntitySessionEventArgs args,
        NCLivingCityAdminActorActionRequest request,
        NCLivingCityAdminMutationStatus status,
        Guid confirmationToken,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(SuccessfulResponse(status), error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminActorActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }

    private sealed record ActorActionExecution(
        NCLivingCityAdminMutationStatus Status,
        NCLivingCityAdminField[] Fields,
        string? Error);
}
