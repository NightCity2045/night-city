// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Groups;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Executes group corrections from the typed F7 protocol. Aggregate versions are always loaded immediately before a
/// mutation, so the UI cannot bypass optimistic concurrency or write directly to persistence.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private async void OnGroupActionRequest(
        NCLivingCityAdminGroupActionRequest request,
        EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanMutate(args))
        {
            SendGroupResult(args, request, NCLivingCityAdminMutationStatus.Unauthorized, Guid.Empty, [],
                "not-authorized");
            return;
        }
        if (!ValidGroupActionRequest(request))
        {
            SendGroupResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "invalid-group-action-request");
            return;
        }
        if (!AcceptRate(args.SenderSession.UserId))
        {
            SendGroupResult(args, request, NCLivingCityAdminMutationStatus.RateLimited, Guid.Empty, [],
                "rate-limited");
            return;
        }

        var fingerprint = GroupActionFingerprint(request);
        if (request.ConfirmationToken == Guid.Empty)
        {
            var token = IssueConfirmation(args.SenderSession.UserId, fingerprint);
            SendGroupResult(args, request, NCLivingCityAdminMutationStatus.ConfirmationRequired, token, [], null);
            return;
        }
        if (!ConsumeConfirmation(args.SenderSession.UserId, request.ConfirmationToken, fingerprint))
        {
            SendGroupResult(args, request, NCLivingCityAdminMutationStatus.Rejected, Guid.Empty, [],
                "confirmation-missing-expired-or-does-not-match");
            return;
        }

        GroupActionExecution execution;
        try
        {
            execution = await ExecuteGroupActionAsync(request, args.SenderSession.UserId);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 group action {request.Action} failed: {exception}");
            execution = new GroupActionExecution(NCLivingCityAdminMutationStatus.Failed, [],
                "storage-or-validation-failure");
        }

        AddAdminAudit(args, $"group.{request.Action}", GroupAuditTarget(request), request.Reason,
            execution.Status.ToString());
        SendGroupResult(args, request, execution.Status, Guid.Empty, execution.Fields, execution.Error);
    }

    private async Task<GroupActionExecution> ExecuteGroupActionAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        return request.Action switch
        {
            NCLivingCityAdminGroupAction.Create => await CreateGroupAsync(request, administrator),
            NCLivingCityAdminGroupAction.AddMember => await AddGroupMemberAsync(request, administrator),
            NCLivingCityAdminGroupAction.RemoveMember => await RemoveGroupMemberAsync(request, administrator),
            NCLivingCityAdminGroupAction.TransferMember => await TransferGroupMemberAsync(request, administrator),
            NCLivingCityAdminGroupAction.SetLeader => await SetGroupLeaderAsync(request, administrator),
            NCLivingCityAdminGroupAction.SetDirective => await SetGroupDirectiveAsync(request, administrator),
            NCLivingCityAdminGroupAction.Materialize => PhysicalizeGroup(request, administrator, true),
            NCLivingCityAdminGroupAction.Dematerialize => PhysicalizeGroup(request, administrator, false),
            _ => RejectGroup("unsupported-group-action"),
        };
    }

    private async Task<GroupActionExecution> CreateGroupAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!Guid.TryParse(request.GroupScopeId.Trim(), out var scope) || scope == Guid.Empty ||
            !_prototypes.HasIndex(new ProtoId<NCCityGroupProfilePrototype>(request.ProfileId.Trim())) ||
            !TryActor(request.LeaderActorId, out var leader) ||
            !NCZoneId.TryParse(request.CurrentZoneId.Trim(), out var zone) || !_zones.TryGetZone(zone, out _) ||
            !TryOptionalOrganization(request.FactionId, out var faction))
        {
            return RejectGroup("invalid-create-group-fields");
        }

        var result = await _groups.CreateAsync(new NCCityGroupCreateRequest(
            request.RequestId,
            scope,
            request.StableLocalId.Trim(),
            request.DisplayName.Trim(),
            new ProtoId<NCCityGroupProfilePrototype>(request.ProfileId.Trim()),
            faction,
            leader,
            request.Role.Trim(),
            zone,
            request.Purpose.Trim(),
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason)));
        return GroupMutation(result);
    }

    private async Task<GroupActionExecution> AddGroupMemberAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!TryGroup(request.GroupId, out var groupId) || !TryActor(request.ActorId, out var actorId))
            return RejectGroup("invalid-group-or-actor-id");
        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
            return RejectGroup("group-not-found");
        return GroupMutation(await _groups.AddMemberAsync(new NCCityGroupAddMemberRequest(
            request.RequestId,
            groupId,
            group.AggregateVersion,
            actorId,
            request.Role.Trim(),
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason))));
    }

    private async Task<GroupActionExecution> RemoveGroupMemberAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!TryGroup(request.GroupId, out var groupId) || !TryActor(request.ActorId, out var actorId) ||
            !TryOptionalActor(request.SuccessorActorId, out var successor))
        {
            return RejectGroup("invalid-group-actor-or-successor-id");
        }
        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
            return RejectGroup("group-not-found");
        return GroupMutation(await _groups.RemoveMemberAsync(new NCCityGroupRemoveMemberRequest(
            request.RequestId,
            groupId,
            group.AggregateVersion,
            actorId,
            successor,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason))));
    }

    private async Task<GroupActionExecution> TransferGroupMemberAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!TryGroup(request.GroupId, out var sourceId) ||
            !TryGroup(request.DestinationGroupId, out var destinationId) ||
            !TryActor(request.ActorId, out var actorId) ||
            !TryOptionalActor(request.SuccessorActorId, out var successor))
        {
            return RejectGroup("invalid-transfer-fields");
        }
        var source = await _groups.GetAsync(sourceId, true);
        var destination = await _groups.GetAsync(destinationId, true);
        if (source == null || destination == null)
            return RejectGroup("source-or-destination-group-not-found");
        return GroupMutation(await _groups.TransferMemberAsync(new NCCityGroupTransferMemberRequest(
            request.RequestId,
            sourceId,
            source.AggregateVersion,
            destinationId,
            destination.AggregateVersion,
            actorId,
            request.Role.Trim(),
            successor,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason))));
    }

    private async Task<GroupActionExecution> SetGroupLeaderAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!TryGroup(request.GroupId, out var groupId) || !TryActor(request.ActorId, out var leader))
            return RejectGroup("invalid-group-or-leader-id");
        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
            return RejectGroup("group-not-found");
        return GroupMutation(await _groups.SetLeaderAsync(new NCCityGroupSetLeaderRequest(
            request.RequestId,
            groupId,
            group.AggregateVersion,
            leader,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason))));
    }

    private async Task<GroupActionExecution> SetGroupDirectiveAsync(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator)
    {
        if (!TryGroup(request.GroupId, out var groupId) ||
            !TryOptionalGuid(request.IncidentId, out var incidentId))
        {
            return RejectGroup("invalid-group-or-incident-id");
        }
        var group = await _groups.GetAsync(groupId, true);
        if (group == null)
            return RejectGroup("group-not-found");
        var purpose = string.IsNullOrWhiteSpace(request.Purpose)
            ? group.Snapshot.Directive.Purpose
            : request.Purpose.Trim();
        var mission = IsNone(request.MissionReference) ? string.Empty : request.MissionReference.Trim();
        return GroupMutation(await _groups.SetDirectiveAsync(new NCCityGroupSetDirectiveRequest(
            request.RequestId,
            groupId,
            group.AggregateVersion,
            purpose,
            request.CurrentOrder.Trim(),
            mission,
            incidentId,
            request.RequestId,
            AuditSource: AdminAuditSource(administrator, request.Reason))));
    }

    private GroupActionExecution PhysicalizeGroup(
        NCLivingCityAdminGroupActionRequest request,
        NetUserId administrator,
        bool materialize)
    {
        if (!TryGroup(request.GroupId, out var groupId))
            return RejectGroup("invalid-group-id");
        var state = _groupPhysicalization.Request(new NCCityGroupPhysicalizationRequest(
            request.RequestId,
            groupId,
            materialize
                ? NCCityGroupPhysicalizationDirection.Materialize
                : NCCityGroupPhysicalizationDirection.Dematerialize,
            NCCityGroupPhysicalizationReason.Admin,
            request.Priority,
            unchecked((uint) groupId.Value.GetHashCode()),
            null,
            AdminAuditSource(administrator, request.Reason)));
        var fields = new FieldBuilder();
        fields.Add("request", "request-id", state.RequestId);
        fields.Add("request", "group-id", state.GroupId);
        fields.Add("request", "direction", state.Direction);
        fields.Add("request", "status", state.Status);
        fields.Add("request", "attempts", state.Attempts);
        fields.Add("request", "members", $"{state.CompletedMembers}/{state.TotalMembers}");
        fields.Add("request", "error", state.Error);
        return new GroupActionExecution(MapPhysicalizationStatus(state.Status), fields.Build(), state.Error);
    }

    private static GroupActionExecution GroupMutation(NCCityGroupMutationResult result)
    {
        var fields = new FieldBuilder();
        fields.Add("result", "group-id", result.GroupId);
        fields.Add("result", "domain-status", result.Status);
        fields.Add("result", "groups-changed", result.Groups.Count);
        if (result.Membership != null)
        {
            fields.Add("membership", "actor-id", result.Membership.Snapshot.ActorId);
            fields.Add("membership", "group-id", result.Membership.Snapshot.GroupId);
            fields.Add("membership", "role", result.Membership.Snapshot.Role);
            fields.Add("membership", "aggregate-version", result.Membership.AggregateVersion);
        }
        foreach (var group in result.Groups)
        {
            fields.Add($"group-{group.Snapshot.GroupId}", "aggregate-version", group.AggregateVersion);
            fields.Add($"group-{group.Snapshot.GroupId}", "members", group.Snapshot.Members.Length);
            fields.Add($"group-{group.Snapshot.GroupId}", "leader", group.Snapshot.LeaderActorId);
        }
        return new GroupActionExecution(MapGroupStatus(result.Status), fields.Build(), result.Error);
    }

    private static NCLivingCityAdminMutationStatus MapGroupStatus(NCCityGroupMutationStatus status) => status switch
    {
        NCCityGroupMutationStatus.Applied => NCLivingCityAdminMutationStatus.Applied,
        NCCityGroupMutationStatus.Duplicate => NCLivingCityAdminMutationStatus.Duplicate,
        NCCityGroupMutationStatus.StorageFailure => NCLivingCityAdminMutationStatus.Failed,
        _ => NCLivingCityAdminMutationStatus.Rejected,
    };

    private static NCLivingCityAdminMutationStatus MapPhysicalizationStatus(
        NCCityGroupPhysicalizationStatus status) => status switch
    {
        NCCityGroupPhysicalizationStatus.Rejected or
        NCCityGroupPhysicalizationStatus.Expired or
        NCCityGroupPhysicalizationStatus.FailedRolledBack or
        NCCityGroupPhysicalizationStatus.PartialFailure => NCLivingCityAdminMutationStatus.Rejected,
        _ => NCLivingCityAdminMutationStatus.Applied,
    };

    private static bool ValidGroupActionRequest(NCLivingCityAdminGroupActionRequest request)
    {
        if (!Enum.IsDefined(request.Action) || request.Priority is < 0 or > 1_000 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        {
            return false;
        }

        return ValidInput(request.GroupId, 64) && ValidInput(request.DestinationGroupId, 64) &&
               ValidInput(request.GroupScopeId, 64) && ValidInput(request.StableLocalId, 128) &&
               ValidInput(request.DisplayName, 128) && ValidInput(request.ProfileId, 128) &&
               ValidInput(request.FactionId, 64) && ValidInput(request.ActorId, 64) &&
               ValidInput(request.LeaderActorId, 64) && ValidInput(request.SuccessorActorId, 64) &&
               ValidInput(request.Role, 128) && ValidInput(request.CurrentZoneId, 160) &&
               ValidInput(request.Purpose, 256) && ValidInput(request.CurrentOrder, 512) &&
               ValidInput(request.MissionReference, 128) && ValidInput(request.IncidentId, 64);
    }

    private static string GroupActionFingerprint(NCLivingCityAdminGroupActionRequest request) => Fingerprint(
        ((byte) request.Action).ToString(),
        request.GroupId.Trim(),
        request.DestinationGroupId.Trim(),
        request.GroupScopeId.Trim(),
        request.StableLocalId.Trim(),
        request.DisplayName.Trim(),
        request.ProfileId.Trim(),
        request.FactionId.Trim(),
        request.ActorId.Trim(),
        request.LeaderActorId.Trim(),
        request.SuccessorActorId.Trim(),
        request.Role.Trim(),
        request.CurrentZoneId.Trim(),
        request.Purpose.Trim(),
        request.CurrentOrder.Trim(),
        request.MissionReference.Trim(),
        request.IncidentId.Trim(),
        request.Priority.ToString(),
        request.Reason.Trim());

    private static string GroupAuditTarget(NCLivingCityAdminGroupActionRequest request) =>
        request.Action == NCLivingCityAdminGroupAction.Create
            ? $"scope={request.GroupScopeId.Trim()}/local={request.StableLocalId.Trim()}"
            : $"group={request.GroupId.Trim()}";

    private static bool TryGroup(string value, out NCGroupId groupId)
    {
        groupId = default;
        if (!Guid.TryParse(value.Trim(), out var raw) || raw == Guid.Empty)
            return false;
        groupId = new NCGroupId(raw);
        return groupId.IsValid;
    }

    private static bool TryOptionalActor(string value, out NCActorId actorId)
    {
        if (IsNone(value))
        {
            actorId = default;
            return true;
        }
        return TryActor(value, out actorId);
    }

    private static bool TryOptionalOrganization(string value, out NCOrganizationId organizationId)
    {
        organizationId = default;
        if (IsNone(value))
            return true;
        if (!Guid.TryParse(value.Trim(), out var raw) || raw == Guid.Empty)
            return false;
        organizationId = new NCOrganizationId(raw);
        return organizationId.IsValid;
    }

    private static bool TryOptionalGuid(string value, out Guid? result)
    {
        result = null;
        if (IsNone(value))
            return true;
        if (!Guid.TryParse(value.Trim(), out var raw) || raw == Guid.Empty)
            return false;
        result = raw;
        return true;
    }

    private static bool IsNone(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    private static GroupActionExecution RejectGroup(string error) =>
        new(NCLivingCityAdminMutationStatus.Rejected, [], error);

    private void SendGroupResult(
        EntitySessionEventArgs args,
        NCLivingCityAdminGroupActionRequest request,
        NCLivingCityAdminMutationStatus status,
        Guid confirmationToken,
        NCLivingCityAdminField[] fields,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(SuccessfulResponse(status), error, fields);
        RaiseNetworkEvent(new NCLivingCityAdminGroupActionResult(
            request.RequestId, request.Action, status, confirmationToken, fields, error), args.SenderSession);
    }

    private sealed record GroupActionExecution(
        NCLivingCityAdminMutationStatus Status,
        NCLivingCityAdminField[] Fields,
        string? Error);
}
