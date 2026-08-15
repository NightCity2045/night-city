// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Groups;

/// <summary>
/// Lazy persistent group registry. Membership commands atomically advance the group aggregate and the actor's
/// membership index so a crash or retry cannot leave one actor in two authoritative groups.
/// </summary>
public sealed partial class NCCityGroupRegistrySystem : EntitySystem
{
    public const string GroupAggregateType = "living-city.group";
    public const string MembershipAggregateType = "living-city.actor-group-membership";
    public const int SnapshotFormatVersion = 1;

    private const int MutationGateStripeCount = 64;
    private const int MaximumStableLocalIdLength = 128;
    private const int MaximumNameLength = 128;
    private const int MaximumRoleLength = 64;
    private const int MaximumDirectiveLength = 256;
    private const int MaximumAuditSourceLength = 128;
    private const int AbsoluteMaximumMembers = 256;
    private const int AbsoluteMaximumHistory = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCGroupId, NCCityGroupRecord> _groupCache = [];
    private readonly ConcurrentDictionary<NCActorId, NCCityActorGroupMembershipRecord> _membershipCache = [];
    // A fixed stripe set bounds synchronization state for 10,000 actors and avoids one semaphore per identity.
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedGroupCount => _groupCache.Count;
    public int CachedMembershipCount => _membershipCache.Count;

    public bool TryGetCached(NCGroupId groupId, out NCCityGroupRecord record)
    {
        return _groupCache.TryGetValue(groupId, out record!);
    }

    public bool TryGetCachedMembership(NCActorId actorId, out NCCityActorGroupMembershipRecord record)
    {
        return _membershipCache.TryGetValue(actorId, out record!);
    }

    public bool Unload(NCGroupId groupId)
    {
        return _groupCache.TryRemove(groupId, out _);
    }

    public bool UnloadMembership(NCActorId actorId)
    {
        return _membershipCache.TryRemove(actorId, out _);
    }

    public async Task<NCCityGroupRecord?> GetAsync(NCGroupId groupId, bool forceReload = false)
    {
        if (!groupId.IsValid)
            return null;
        if (!forceReload && _groupCache.TryGetValue(groupId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(GroupAggregateType, GroupKey(groupId));
        var record = DeserializeGroup(stored, groupId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCCityActorGroupMembershipRecord?> GetMembershipAsync(
        NCActorId actorId,
        bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _membershipCache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(MembershipAggregateType, ActorKey(actorId));
        var record = DeserializeMembership(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCCityGroupPage> GetPageAsync(NCGroupId? afterGroupId, int limit, bool cache = false)
    {
        var page = await _database.GetNCAggregateSnapshotPageAsync(
            GroupAggregateType,
            afterGroupId is { IsValid: true } after ? GroupKey(after) : null,
            limit);
        var records = new List<NCCityGroupRecord>(page.Count);
        foreach (var stored in page)
        {
            if (!Guid.TryParse(stored.AggregateId, out var rawId))
                continue;
            var record = DeserializeGroup(stored, new NCGroupId(rawId));
            if (record == null)
                continue;
            records.Add(record);
            if (cache)
                CacheNewest(record);
        }

        NCGroupId? continuation = null;
        if (page.Count > 0 && Guid.TryParse(page[^1].AggregateId, out var lastGroupId))
            continuation = new NCGroupId(lastGroupId);
        return new NCCityGroupPage(records, continuation, page.Count == limit);
    }

    public async Task<NCCityGroupMutationResult> CreateAsync(NCCityGroupCreateRequest request)
    {
        var groupId = request.GroupScopeId == Guid.Empty || string.IsNullOrWhiteSpace(request.StableLocalId)
            ? default
            : CreateDeterministicId(request.GroupScopeId, request.StableLocalId);
        if (!TryValidateCreate(request, out var profile, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(
                groupId,
                _clock.IsReady ? NCCityGroupMutationStatus.Rejected : NCCityGroupMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var gates = await AcquireGates(groupId.Value, request.LeaderActorId.Value);
        try
        {
            var currentGroup = await GetAsync(groupId);
            var currentMembership = await GetMembershipAsync(request.LeaderActorId);
            if (currentGroup != null)
            {
                // Crossing the persistence boundary is required for exact replay of a completed create command.
                return await PersistBatch(
                    groupId,
                    request.CommandId,
                    "living-city.group.create",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource,
                    epoch,
                    now,
                    [new GroupWrite(currentGroup.Snapshot, 0, Guid.NewGuid(), "living-city.group.created")],
                    CreateReplayMembershipWrite(request.LeaderActorId, currentMembership, now));
            }

            if (currentMembership?.Snapshot.GroupId.IsValid == true)
                return Reject(groupId, NCCityGroupMutationStatus.Rejected, "The leader already belongs to a group.");

            var leader = await _actors.GetAsync(request.LeaderActorId);
            if (!CanJoinGroup(leader))
                return Reject(groupId, NCCityGroupMutationStatus.Rejected, "The leader actor is unavailable for membership.");

            var groupEventId = Guid.NewGuid();
            var membershipEventId = Guid.NewGuid();
            var role = request.LeaderRole.Trim();
            var member = new NCCityGroupMember(request.LeaderActorId, role, now);
            var snapshot = new NCCityGroupSnapshot(
                groupId,
                request.StableLocalId.Trim(),
                request.DisplayName.Trim(),
                profile.ID,
                request.FactionId,
                request.LeaderActorId,
                [member],
                [new NCCityGroupMembershipHistoryEntry(
                    request.LeaderActorId,
                    role,
                    now,
                    null,
                    membershipEventId,
                    null)],
                new NCCityGroupDirective(request.Purpose.Trim(), string.Empty, string.Empty, null),
                new NCCityGroupRoute(request.CurrentZone, default, NCCityTravelMode.None, null),
                new NCCityGroupResourceSummary(
                    profile.InitialTransportCapacity,
                    profile.InitialCargoUnits,
                    profile.InitialSupplyUnits,
                    profile.InitialMorale),
                profile.FormationPolicy,
                profile.MaterializeTogether,
                NCCityGroupAlertState.Unaware,
                NCCityGroupStatus.Active,
                1,
                groupEventId);
            var membership = new NCCityActorGroupMembershipSnapshot(
                request.LeaderActorId,
                groupId,
                role,
                now,
                now,
                (currentMembership?.Snapshot.DataVersion ?? 0) + 1,
                membershipEventId);
            return await PersistBatch(
                groupId,
                request.CommandId,
                "living-city.group.create",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now,
                [new GroupWrite(snapshot, 0, groupEventId, "living-city.group.created")],
                new MembershipWrite(
                    membership,
                    currentMembership?.AggregateVersion ?? 0,
                    membershipEventId,
                    "living-city.group.member-joined"));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create persistent group {groupId}: {exception}");
            return Reject(groupId, NCCityGroupMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCCityGroupMutationResult> AddMemberAsync(NCCityGroupAddMemberRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.GroupId,
                request.CommandId,
                request.ExpectedGroupVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !TryValidateRole(request.ActorId, request.Role, out error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(
                request.GroupId,
                _clock.IsReady ? NCCityGroupMutationStatus.Rejected : NCCityGroupMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var gates = await AcquireGates(request.GroupId.Value, request.ActorId.Value);
        try
        {
            var group = await GetAsync(request.GroupId);
            if (group == null)
                return Reject(request.GroupId, NCCityGroupMutationStatus.NotFound, "Persistent group does not exist.");
            var membership = await GetMembershipAsync(request.ActorId);
            if (group.AggregateVersion != request.ExpectedGroupVersion)
            {
                return await PersistBatch(
                    request.GroupId,
                    request.CommandId,
                    "living-city.group.add-member",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource,
                    epoch,
                    now,
                    [new GroupWrite(group.Snapshot, request.ExpectedGroupVersion, Guid.NewGuid(),
                        "living-city.group.member-added")],
                    CreateReplayMembershipWrite(request.ActorId, membership, now));
            }

            if (membership?.Snapshot.GroupId.IsValid == true)
                return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected, "The actor already belongs to a group.");
            if (!TryGetUsableProfile(group.Snapshot.ProfileId, out var profile, out error) ||
                group.Snapshot.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived ||
                group.Snapshot.Members.Length >= profile.MaximumMembers ||
                group.Snapshot.Members.Any(member => member.ActorId == request.ActorId))
            {
                return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected,
                    error ?? "The group cannot accept this actor.");
            }

            var actor = await _actors.GetAsync(request.ActorId);
            if (!CanJoinGroup(actor))
                return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected, "The actor is unavailable for membership.");

            var groupEventId = Guid.NewGuid();
            var membershipEventId = Guid.NewGuid();
            var role = request.Role.Trim();
            var nextGroup = group.Snapshot with
            {
                Members = group.Snapshot.Members
                    .Append(new NCCityGroupMember(request.ActorId, role, now))
                    .OrderBy(static member => member.ActorId.Value)
                    .ToArray(),
                MembershipHistory = AppendHistory(
                    group.Snapshot.MembershipHistory,
                    new NCCityGroupMembershipHistoryEntry(
                        request.ActorId,
                        role,
                        now,
                        null,
                        membershipEventId,
                        null),
                    profile.MaximumSnapshotHistory),
                DataVersion = checked(group.Snapshot.DataVersion + 1),
                LastAppliedEventId = groupEventId,
            };
            var nextMembership = new NCCityActorGroupMembershipSnapshot(
                request.ActorId,
                request.GroupId,
                role,
                now,
                now,
                (membership?.Snapshot.DataVersion ?? 0) + 1,
                membershipEventId);
            return await PersistBatch(
                request.GroupId,
                request.CommandId,
                "living-city.group.add-member",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now,
                [new GroupWrite(nextGroup, request.ExpectedGroupVersion, groupEventId,
                    "living-city.group.member-added")],
                new MembershipWrite(
                    nextMembership,
                    membership?.AggregateVersion ?? 0,
                    membershipEventId,
                    "living-city.group.member-joined"));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to add actor {request.ActorId} to group {request.GroupId}: {exception}");
            return Reject(request.GroupId, NCCityGroupMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCCityGroupMutationResult> RemoveMemberAsync(NCCityGroupRemoveMemberRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.GroupId,
                request.CommandId,
                request.ExpectedGroupVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !request.ActorId.IsValid ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(
                request.GroupId,
                _clock.IsReady ? NCCityGroupMutationStatus.Rejected : NCCityGroupMutationStatus.ClockUnavailable,
                error ?? "Actor identity or active-world clock is invalid.");
        }

        var gates = await AcquireGates(request.GroupId.Value, request.ActorId.Value);
        try
        {
            var group = await GetAsync(request.GroupId);
            if (group == null)
                return Reject(request.GroupId, NCCityGroupMutationStatus.NotFound, "Persistent group does not exist.");
            var membership = await GetMembershipAsync(request.ActorId);
            if (group.AggregateVersion != request.ExpectedGroupVersion)
            {
                return await PersistBatch(
                    request.GroupId,
                    request.CommandId,
                    "living-city.group.remove-member",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource,
                    epoch,
                    now,
                    [new GroupWrite(group.Snapshot, request.ExpectedGroupVersion, Guid.NewGuid(),
                        "living-city.group.member-removed")],
                    CreateReplayMembershipWrite(request.ActorId, membership, now));
            }

            if (membership == null || membership.Snapshot.GroupId != request.GroupId ||
                group.Snapshot.Members.All(member => member.ActorId != request.ActorId) ||
                !TryResolveLeaderAfterDeparture(
                    group.Snapshot,
                    request.ActorId,
                    request.SuccessorLeaderActorId,
                    out var nextLeader,
                    out error))
            {
                return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected,
                    error ?? "Actor membership does not match the group.");
            }

            var groupEventId = Guid.NewGuid();
            var membershipEventId = Guid.NewGuid();
            var members = group.Snapshot.Members
                .Where(member => member.ActorId != request.ActorId)
                .ToArray();
            var nextGroup = group.Snapshot with
            {
                LeaderActorId = nextLeader,
                Members = members,
                MembershipHistory = CloseHistory(
                    group.Snapshot.MembershipHistory,
                    request.ActorId,
                    now,
                    membershipEventId),
                Status = members.Length == 0 ? NCCityGroupStatus.Disbanded : group.Snapshot.Status,
                DataVersion = checked(group.Snapshot.DataVersion + 1),
                LastAppliedEventId = groupEventId,
            };
            var nextMembership = membership.Snapshot with
            {
                GroupId = default,
                Role = string.Empty,
                JoinedAt = null,
                ChangedAt = now,
                DataVersion = checked(membership.Snapshot.DataVersion + 1),
                LastAppliedEventId = membershipEventId,
            };
            return await PersistBatch(
                request.GroupId,
                request.CommandId,
                "living-city.group.remove-member",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now,
                [new GroupWrite(nextGroup, request.ExpectedGroupVersion, groupEventId,
                    "living-city.group.member-removed")],
                new MembershipWrite(
                    nextMembership,
                    membership.AggregateVersion,
                    membershipEventId,
                    "living-city.group.member-left"));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to remove actor {request.ActorId} from group {request.GroupId}: {exception}");
            return Reject(request.GroupId, NCCityGroupMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCCityGroupMutationResult> TransferMemberAsync(NCCityGroupTransferMemberRequest request)
    {
        if (!TryValidateTransfer(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(
                request.SourceGroupId,
                _clock.IsReady ? NCCityGroupMutationStatus.Rejected : NCCityGroupMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var gates = await AcquireGates(
            request.SourceGroupId.Value,
            request.DestinationGroupId.Value,
            request.ActorId.Value);
        try
        {
            var source = await GetAsync(request.SourceGroupId);
            var destination = await GetAsync(request.DestinationGroupId);
            if (source == null || destination == null)
                return Reject(request.SourceGroupId, NCCityGroupMutationStatus.NotFound,
                    "The source or destination group does not exist.");
            var membership = await GetMembershipAsync(request.ActorId);
            if (source.AggregateVersion != request.ExpectedSourceVersion ||
                destination.AggregateVersion != request.ExpectedDestinationVersion)
            {
                return await PersistBatch(
                    request.SourceGroupId,
                    request.CommandId,
                    "living-city.group.transfer-member",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource,
                    epoch,
                    now,
                    [
                        new GroupWrite(source.Snapshot, request.ExpectedSourceVersion, Guid.NewGuid(),
                            "living-city.group.member-transferred-out"),
                        new GroupWrite(destination.Snapshot, request.ExpectedDestinationVersion, Guid.NewGuid(),
                            "living-city.group.member-transferred-in"),
                    ],
                    CreateReplayMembershipWrite(request.ActorId, membership, now));
            }

            if (membership == null || membership.Snapshot.GroupId != request.SourceGroupId ||
                source.Snapshot.Members.All(member => member.ActorId != request.ActorId) ||
                destination.Snapshot.Members.Any(member => member.ActorId == request.ActorId) ||
                destination.Snapshot.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived ||
                !TryGetUsableProfile(destination.Snapshot.ProfileId, out var destinationProfile, out error) ||
                destination.Snapshot.Members.Length >= destinationProfile.MaximumMembers ||
                !TryResolveLeaderAfterDeparture(
                    source.Snapshot,
                    request.ActorId,
                    request.SourceSuccessorLeaderActorId,
                    out var nextSourceLeader,
                    out error))
            {
                return Reject(request.SourceGroupId, NCCityGroupMutationStatus.Rejected,
                    error ?? "The membership transfer violates a group invariant.");
            }

            var sourceEventId = Guid.NewGuid();
            var destinationEventId = Guid.NewGuid();
            var membershipEventId = Guid.NewGuid();
            var sourceMembers = source.Snapshot.Members
                .Where(member => member.ActorId != request.ActorId)
                .ToArray();
            var role = request.DestinationRole.Trim();
            var nextSource = source.Snapshot with
            {
                LeaderActorId = nextSourceLeader,
                Members = sourceMembers,
                MembershipHistory = CloseHistory(
                    source.Snapshot.MembershipHistory,
                    request.ActorId,
                    now,
                    membershipEventId),
                Status = sourceMembers.Length == 0 ? NCCityGroupStatus.Disbanded : source.Snapshot.Status,
                DataVersion = checked(source.Snapshot.DataVersion + 1),
                LastAppliedEventId = sourceEventId,
            };
            var nextDestination = destination.Snapshot with
            {
                Members = destination.Snapshot.Members
                    .Append(new NCCityGroupMember(request.ActorId, role, now))
                    .OrderBy(static member => member.ActorId.Value)
                    .ToArray(),
                MembershipHistory = AppendHistory(
                    destination.Snapshot.MembershipHistory,
                    new NCCityGroupMembershipHistoryEntry(
                        request.ActorId,
                        role,
                        now,
                        null,
                        membershipEventId,
                        null),
                    destinationProfile.MaximumSnapshotHistory),
                DataVersion = checked(destination.Snapshot.DataVersion + 1),
                LastAppliedEventId = destinationEventId,
            };
            var nextMembership = membership.Snapshot with
            {
                GroupId = request.DestinationGroupId,
                Role = role,
                JoinedAt = now,
                ChangedAt = now,
                DataVersion = checked(membership.Snapshot.DataVersion + 1),
                LastAppliedEventId = membershipEventId,
            };
            return await PersistBatch(
                request.SourceGroupId,
                request.CommandId,
                "living-city.group.transfer-member",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now,
                [
                    new GroupWrite(nextSource, request.ExpectedSourceVersion, sourceEventId,
                        "living-city.group.member-transferred-out"),
                    new GroupWrite(nextDestination, request.ExpectedDestinationVersion, destinationEventId,
                        "living-city.group.member-transferred-in"),
                ],
                new MembershipWrite(
                    nextMembership,
                    membership.AggregateVersion,
                    membershipEventId,
                    "living-city.group.membership-transferred"));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to transfer actor {request.ActorId} between groups: {exception}");
            return Reject(request.SourceGroupId, NCCityGroupMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCCityGroupMutationResult> SetLeaderAsync(NCCityGroupSetLeaderRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.GroupId,
                request.CommandId,
                request.ExpectedGroupVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !request.LeaderActorId.IsValid)
        {
            return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected,
                error ?? "Leader actor identity is invalid.");
        }

        return await MutateSingleGroup(
            request.GroupId,
            request.CommandId,
            request.ExpectedGroupVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.group.set-leader",
            "living-city.group.leader-changed",
            (current, _, eventId) =>
            {
                if (current.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived ||
                    current.Members.All(member => member.ActorId != request.LeaderActorId))
                {
                    return null;
                }

                return current with
                {
                    LeaderActorId = request.LeaderActorId,
                    DataVersion = checked(current.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
            });
    }

    public async Task<NCCityGroupMutationResult> SetDirectiveAsync(NCCityGroupSetDirectiveRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.GroupId,
                request.CommandId,
                request.ExpectedGroupVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !TryValidateDirective(request.Purpose, request.CurrentOrder, request.MissionReference, out error))
        {
            return Reject(request.GroupId, NCCityGroupMutationStatus.Rejected, error!);
        }

        return await MutateSingleGroup(
            request.GroupId,
            request.CommandId,
            request.ExpectedGroupVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.group.set-directive",
            "living-city.group.directive-changed",
            (current, _, eventId) =>
            {
                if (current.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived)
                    return null;
                return current with
                {
                    Directive = new NCCityGroupDirective(
                        request.Purpose.Trim(),
                        request.CurrentOrder.Trim(),
                        request.MissionReference.Trim(),
                        request.IncidentId),
                    DataVersion = checked(current.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
            });
    }

    public static NCGroupId CreateDeterministicId(Guid groupScopeId, string stableLocalId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(groupScopeId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLocalId);
        Span<byte> scope = stackalloc byte[16];
        groupScopeId.TryWriteBytes(scope);
        var local = Encoding.UTF8.GetBytes(stableLocalId.Trim());
        var input = new byte[scope.Length + local.Length];
        scope.CopyTo(input);
        local.CopyTo(input.AsSpan(scope.Length));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new NCGroupId(new Guid(hash[..16]));
    }

    private async Task<NCCityGroupMutationResult> MutateSingleGroup(
        NCGroupId groupId,
        Guid commandId,
        long expectedVersion,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        string commandType,
        string eventType,
        Func<NCCityGroupSnapshot, NCActiveWorldTime, Guid, NCCityGroupSnapshot?> transform)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(groupId, NCCityGroupMutationStatus.ClockUnavailable, "The active-world clock is not ready.");

        var gates = await AcquireGates(groupId.Value);
        try
        {
            var current = await GetAsync(groupId);
            if (current == null)
                return Reject(groupId, NCCityGroupMutationStatus.NotFound, "Persistent group does not exist.");
            if (current.AggregateVersion != expectedVersion)
            {
                return await PersistBatch(
                    groupId,
                    commandId,
                    commandType,
                    correlationId,
                    causationId,
                    auditSource,
                    epoch,
                    now,
                    [new GroupWrite(current.Snapshot, expectedVersion, Guid.NewGuid(), eventType)],
                    null);
            }

            var eventId = Guid.NewGuid();
            var next = transform(current.Snapshot, now, eventId);
            if (next == null)
                return Reject(groupId, NCCityGroupMutationStatus.Rejected, "The group lifecycle rejects this mutation.");
            return await PersistBatch(
                groupId,
                commandId,
                commandType,
                correlationId,
                causationId,
                auditSource,
                epoch,
                now,
                [new GroupWrite(next, expectedVersion, eventId, eventType)],
                null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to mutate persistent group {groupId}: {exception}");
            return Reject(groupId, NCCityGroupMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private async Task<NCCityGroupMutationResult> PersistBatch(
        NCGroupId primaryGroupId,
        Guid commandId,
        string commandType,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now,
        IReadOnlyList<GroupWrite> groups,
        MembershipWrite? membership)
    {
        var receiptGroups = groups
            .Select(write => new NCCityGroupReceiptRecord(write.Snapshot, write.ExpectedVersion + 1))
            .ToArray();
        var receiptMembership = membership == null
            ? null
            : new NCCityMembershipReceiptRecord(membership.Snapshot, membership.ExpectedVersion + 1);
        var writes = new List<NCPersistentAggregateWrite>(groups.Count + (membership == null ? 0 : 1));
        foreach (var group in groups)
        {
            writes.Add(new NCPersistentAggregateWrite(
                group.EventId,
                group.EventType,
                GroupAggregateType,
                GroupKey(group.Snapshot.GroupId),
                group.ExpectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(group.Snapshot, JsonOptions),
                JsonSerializer.Serialize(
                    new NCCityGroupEventPayload(
                        group.Snapshot.GroupId,
                        group.EventType,
                        auditSource,
                        group.Snapshot),
                    JsonOptions)));
        }

        if (membership != null)
        {
            writes.Add(new NCPersistentAggregateWrite(
                membership.EventId,
                membership.EventType,
                MembershipAggregateType,
                ActorKey(membership.Snapshot.ActorId),
                membership.ExpectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(membership.Snapshot, JsonOptions),
                JsonSerializer.Serialize(
                    new NCCityMembershipEventPayload(
                        membership.Snapshot.ActorId,
                        membership.Snapshot.GroupId,
                        membership.EventType,
                        auditSource,
                        membership.Snapshot),
                    JsonOptions)));
        }

        var receiptPayload = JsonSerializer.Serialize(
            new NCCityGroupBatchReceiptPayload(receiptGroups, receiptMembership, epoch, now),
            JsonOptions);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            commandId,
            commandType,
            "group",
            GroupKey(primaryGroupId),
            writes,
            receiptPayload,
            correlationId,
            causationId,
            epoch,
            now,
            []));

        if (result.Status == NCPersistentMutationStatus.Applied)
            return ApplyReceipt(primaryGroupId, receiptPayload, NCCityGroupMutationStatus.Applied);
        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var duplicate = ApplyReceipt(primaryGroupId, result.ResultPayload, NCCityGroupMutationStatus.Duplicate);
            if (duplicate.Groups.Count > 0)
                return duplicate;

            // A damaged receipt result cannot be trusted; reload only the bounded identities named by the command.
            foreach (var group in groups)
                await GetAsync(group.Snapshot.GroupId, true);
            if (membership != null)
                await GetMembershipAsync(membership.Snapshot.ActorId, true);
            return Reject(primaryGroupId, NCCityGroupMutationStatus.StorageFailure,
                "The completed group command receipt payload is invalid.");
        }

        var status = result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCCityGroupMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCCityGroupMutationStatus.ClockUnavailable,
            NCPersistentMutationStatus.DuplicateFailed when
                result.Error?.StartsWith("One or more aggregate versions ") == true =>
                NCCityGroupMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when
                result.Error?.StartsWith("The batch timestamp ") == true =>
                NCCityGroupMutationStatus.ClockUnavailable,
            _ => NCCityGroupMutationStatus.StorageFailure,
        };
        return Reject(primaryGroupId, status, result.Error ?? "The persistent group mutation failed.");
    }

    private NCCityGroupMutationResult ApplyReceipt(
        NCGroupId primaryGroupId,
        string? payload,
        NCCityGroupMutationStatus status)
    {
        var receipt = DeserializeReceipt(payload);
        if (receipt == null)
            return Reject(primaryGroupId, NCCityGroupMutationStatus.StorageFailure,
                "The group command receipt payload is invalid.");

        var groups = new List<NCCityGroupRecord>(receipt.Groups.Length);
        foreach (var group in receipt.Groups)
        {
            if (!TryNormalizeGroup(group.Snapshot, group.Snapshot.GroupId, out var normalized) ||
                group.AggregateVersion < 1)
            {
                return Reject(primaryGroupId, NCCityGroupMutationStatus.StorageFailure,
                    "The group command receipt contains an invalid group snapshot.");
            }

            var record = CacheNewest(new NCCityGroupRecord(
                normalized,
                group.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt));
            groups.Add(record);
            PublishPersisted(record);
        }

        NCCityActorGroupMembershipRecord? membership = null;
        if (receipt.Membership != null)
        {
            var value = receipt.Membership;
            if (!TryNormalizeMembership(value.Snapshot, value.Snapshot.ActorId, out var normalized) ||
                value.AggregateVersion < 1)
            {
                return Reject(primaryGroupId, NCCityGroupMutationStatus.StorageFailure,
                    "The group command receipt contains an invalid membership snapshot.");
            }

            membership = CacheNewest(new NCCityActorGroupMembershipRecord(
                normalized,
                value.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt));
        }

        return new NCCityGroupMutationResult(status, primaryGroupId, groups, membership, null);
    }

    private bool TryValidateCreate(
        NCCityGroupCreateRequest request,
        out NCCityGroupProfilePrototype profile,
        out string? error)
    {
        profile = default!;
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty ||
            request.GroupScopeId == Guid.Empty || !request.LeaderActorId.IsValid ||
            string.IsNullOrWhiteSpace(request.StableLocalId) ||
            request.StableLocalId.Length > MaximumStableLocalIdLength ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > MaximumNameLength ||
            string.IsNullOrWhiteSpace(request.LeaderRole) || request.LeaderRole.Length > MaximumRoleLength ||
            request.Purpose == null || request.Purpose.Length > MaximumDirectiveLength ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > MaximumAuditSourceLength ||
            !_zones.TryGetZone(request.CurrentZone, out _))
        {
            error = "Group identity, leader, location, or command metadata is invalid.";
            return false;
        }

        return TryGetUsableProfile(request.Profile.Id, out profile, out error);
    }

    private bool TryGetUsableProfile(
        string profileId,
        out NCCityGroupProfilePrototype profile,
        out string? error)
    {
        profile = default!;
        if (string.IsNullOrWhiteSpace(profileId) ||
            !_prototypes.TryIndex(new ProtoId<NCCityGroupProfilePrototype>(profileId), out var loaded) ||
            loaded == null)
        {
            error = $"Unknown group profile {profileId}.";
            return false;
        }

        if (loaded.MaximumMembers is < 1 or > AbsoluteMaximumMembers ||
            loaded.MaximumSnapshotHistory < loaded.MaximumMembers ||
            loaded.MaximumSnapshotHistory > AbsoluteMaximumHistory ||
            loaded.InitialTransportCapacity < 0 || loaded.InitialCargoUnits < 0 || loaded.InitialSupplyUnits < 0 ||
            !float.IsFinite(loaded.InitialMorale) ||
            !Enum.IsDefined(loaded.FormationPolicy))
        {
            error = $"Group profile {profileId} contains invalid structural limits or initial resources.";
            return false;
        }

        profile = loaded;
        error = null;
        return true;
    }

    private static bool TryValidateMutationMetadata(
        NCGroupId groupId,
        Guid commandId,
        long expectedVersion,
        Guid correlationId,
        string auditSource,
        out string? error)
    {
        if (!groupId.IsValid || commandId == Guid.Empty || expectedVersion < 1 || correlationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > MaximumAuditSourceLength)
        {
            error = "Group identity, version, or command metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateRole(NCActorId actorId, string role, out string? error)
    {
        if (!actorId.IsValid || string.IsNullOrWhiteSpace(role) || role.Length > MaximumRoleLength)
        {
            error = "Actor identity or group role is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateDirective(
        string purpose,
        string currentOrder,
        string missionReference,
        out string? error)
    {
        if (purpose == null || currentOrder == null || missionReference == null ||
            purpose.Length > MaximumDirectiveLength || currentOrder.Length > MaximumDirectiveLength ||
            missionReference.Length > MaximumDirectiveLength)
        {
            error = "Group directive fields exceed their structural bounds.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateTransfer(NCCityGroupTransferMemberRequest request, out string? error)
    {
        if (!TryValidateMutationMetadata(
                request.SourceGroupId,
                request.CommandId,
                request.ExpectedSourceVersion,
                request.CorrelationId,
                request.AuditSource,
                out error) ||
            !request.DestinationGroupId.IsValid ||
            request.DestinationGroupId == request.SourceGroupId ||
            request.ExpectedDestinationVersion < 1 ||
            !TryValidateRole(request.ActorId, request.DestinationRole, out error))
        {
            error ??= "Membership transfer metadata is invalid.";
            return false;
        }

        return true;
    }

    private static bool CanJoinGroup(NCCityActorRecord? actor)
    {
        return actor != null &&
               !actor.Snapshot.Lifecycle.Frozen &&
               actor.Snapshot.Lifecycle.Status is not (NCCityActorStatus.Dead or NCCityActorStatus.Archived);
    }

    private static bool TryResolveLeaderAfterDeparture(
        NCCityGroupSnapshot group,
        NCActorId departingActorId,
        NCActorId requestedSuccessor,
        out NCActorId nextLeader,
        out string? error)
    {
        nextLeader = group.LeaderActorId;
        error = null;
        if (group.LeaderActorId != departingActorId)
            return true;

        var remaining = group.Members.Where(member => member.ActorId != departingActorId).ToArray();
        if (remaining.Length == 0)
        {
            nextLeader = default;
            return true;
        }

        if (!requestedSuccessor.IsValid || remaining.All(member => member.ActorId != requestedSuccessor))
        {
            error = "Removing or transferring the leader requires a successor from the remaining members.";
            return false;
        }

        nextLeader = requestedSuccessor;
        return true;
    }

    private static NCCityGroupMembershipHistoryEntry[] AppendHistory(
        NCCityGroupMembershipHistoryEntry[]? current,
        NCCityGroupMembershipHistoryEntry entry,
        int limit)
    {
        return (current ?? [])
            .Append(entry)
            .TakeLast(limit)
            .ToArray();
    }

    private static NCCityGroupMembershipHistoryEntry[] CloseHistory(
        NCCityGroupMembershipHistoryEntry[]? current,
        NCActorId actorId,
        NCActiveWorldTime leftAt,
        Guid leftEventId)
    {
        var history = (current ?? []).ToArray();
        for (var i = history.Length - 1; i >= 0; i--)
        {
            if (history[i].ActorId != actorId || history[i].LeftAt != null)
                continue;
            history[i] = history[i] with { LeftAt = leftAt, LeftEventId = leftEventId };
            break;
        }

        return history;
    }

    private static MembershipWrite CreateReplayMembershipWrite(
        NCActorId actorId,
        NCCityActorGroupMembershipRecord? membership,
        NCActiveWorldTime now)
    {
        var eventId = Guid.NewGuid();
        var snapshot = membership?.Snapshot ?? new NCCityActorGroupMembershipSnapshot(
            actorId,
            default,
            string.Empty,
            null,
            now,
            1,
            eventId);
        return new MembershipWrite(
            snapshot,
            membership?.AggregateVersion ?? 0,
            eventId,
            "living-city.group.membership-replay-probe");
    }

    private static NCCityGroupRecord? DeserializeGroup(NCAggregateSnapshotData? stored, NCGroupId expectedGroupId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityGroupSnapshot>(stored.Payload, JsonOptions);
            if (!TryNormalizeGroup(snapshot, expectedGroupId, out var normalized))
                return null;
            return new NCCityGroupRecord(
                normalized,
                stored.AggregateVersion,
                stored.SessionEpoch,
                stored.ActiveWorldTime);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCCityActorGroupMembershipRecord? DeserializeMembership(
        NCAggregateSnapshotData? stored,
        NCActorId expectedActorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityActorGroupMembershipSnapshot>(stored.Payload, JsonOptions);
            if (!TryNormalizeMembership(snapshot, expectedActorId, out var normalized))
                return null;
            return new NCCityActorGroupMembershipRecord(
                normalized,
                stored.AggregateVersion,
                stored.SessionEpoch,
                stored.ActiveWorldTime);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCCityGroupBatchReceiptPayload? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<NCCityGroupBatchReceiptPayload>(payload, JsonOptions);
            return receipt is { Groups: not null } && receipt.SessionEpoch.IsValid && receipt.PersistedAt.IsValid
                ? receipt
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryNormalizeGroup(
        NCCityGroupSnapshot? snapshot,
        NCGroupId expectedGroupId,
        out NCCityGroupSnapshot normalized)
    {
        normalized = default!;
        if (snapshot == null || snapshot.GroupId != expectedGroupId || !snapshot.GroupId.IsValid ||
            string.IsNullOrWhiteSpace(snapshot.StableLocalId) ||
            snapshot.StableLocalId.Length > MaximumStableLocalIdLength ||
            string.IsNullOrWhiteSpace(snapshot.DisplayName) ||
            snapshot.DisplayName.Length > MaximumNameLength ||
            string.IsNullOrWhiteSpace(snapshot.ProfileId) ||
            snapshot.Directive == null || snapshot.Route == null || snapshot.Resources == null ||
            snapshot.Directive.Purpose == null || snapshot.Directive.CurrentOrder == null ||
            snapshot.Directive.MissionReference == null ||
            snapshot.Directive.Purpose.Length > MaximumDirectiveLength ||
            snapshot.Directive.CurrentOrder.Length > MaximumDirectiveLength ||
            snapshot.Directive.MissionReference.Length > MaximumDirectiveLength ||
            !snapshot.Route.CurrentZone.IsValid || snapshot.DataVersion < 1 || snapshot.LastAppliedEventId == Guid.Empty ||
            snapshot.Route.ExpectedArrivalAt is { IsValid: false } ||
            (snapshot.Route.DestinationZone.IsValid && !HasSingleTravelMode(snapshot.Route.TravelMode)) ||
            (!snapshot.Route.DestinationZone.IsValid &&
             (snapshot.Route.TravelMode != NCCityTravelMode.None || snapshot.Route.ExpectedArrivalAt != null)) ||
            snapshot.Resources.TransportCapacity < 0 || snapshot.Resources.CargoUnits < 0 ||
            snapshot.Resources.SupplyUnits < 0 || !float.IsFinite(snapshot.Resources.Morale) ||
            !Enum.IsDefined(snapshot.FormationPolicy) || !Enum.IsDefined(snapshot.AlertState) ||
            !Enum.IsDefined(snapshot.Status))
        {
            return false;
        }

        var members = snapshot.Members ?? [];
        var history = snapshot.MembershipHistory ?? [];
        if (members.Length > AbsoluteMaximumMembers || history.Length > AbsoluteMaximumHistory ||
            members.Any(member => member == null || !member.ActorId.IsValid ||
                string.IsNullOrWhiteSpace(member.Role) || member.Role.Length > MaximumRoleLength ||
                !member.JoinedAt.IsValid) ||
            members.Select(member => member.ActorId).Distinct().Count() != members.Length ||
            history.Any(entry => entry == null || !entry.ActorId.IsValid ||
                string.IsNullOrWhiteSpace(entry.Role) || entry.Role.Length > MaximumRoleLength ||
                !entry.JoinedAt.IsValid ||
                entry.JoinedEventId == Guid.Empty || entry.LeftAt is { IsValid: false } ||
                (entry.LeftAt != null && (entry.LeftAt < entry.JoinedAt || entry.LeftEventId == null))))
        {
            return false;
        }

        if (snapshot.Status == NCCityGroupStatus.Disbanded)
        {
            if (members.Length != 0 || snapshot.LeaderActorId.IsValid)
                return false;
        }
        else if (!snapshot.LeaderActorId.IsValid || members.All(member => member.ActorId != snapshot.LeaderActorId))
        {
            return false;
        }

        normalized = snapshot with { Members = members, MembershipHistory = history };
        return true;
    }

    private static bool TryNormalizeMembership(
        NCCityActorGroupMembershipSnapshot? snapshot,
        NCActorId expectedActorId,
        out NCCityActorGroupMembershipSnapshot normalized)
    {
        normalized = default!;
        if (snapshot == null || snapshot.ActorId != expectedActorId || !snapshot.ActorId.IsValid ||
            !snapshot.ChangedAt.IsValid || snapshot.DataVersion < 1 || snapshot.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        if (snapshot.GroupId.IsValid)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Role) || snapshot.JoinedAt is not { IsValid: true })
                return false;
        }
        else if (!string.IsNullOrEmpty(snapshot.Role) || snapshot.JoinedAt != null)
        {
            return false;
        }

        normalized = snapshot;
        return true;
    }

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private async Task<int[]> AcquireGates(params Guid[] identities)
    {
        var indices = identities
            .Select(identity => (identity.GetHashCode() & int.MaxValue) % _mutationGates.Length)
            .Distinct()
            .Order()
            .ToArray();
        foreach (var index in indices)
            await _mutationGates[index].WaitAsync();
        return indices;
    }

    private void ReleaseGates(IReadOnlyList<int> indices)
    {
        for (var i = indices.Count - 1; i >= 0; i--)
            _mutationGates[indices[i]].Release();
    }

    private NCCityGroupRecord CacheNewest(NCCityGroupRecord candidate)
    {
        return _groupCache.AddOrUpdate(
            candidate.Snapshot.GroupId,
            candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private NCCityActorGroupMembershipRecord CacheNewest(NCCityActorGroupMembershipRecord candidate)
    {
        return _membershipCache.AddOrUpdate(
            candidate.Snapshot.ActorId,
            candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private void PublishPersisted(NCCityGroupRecord record)
    {
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCCityGroupPersistedEvent(
            record.Snapshot.GroupId,
            record.AggregateVersion,
            record.Snapshot.Status,
            record.Snapshot.Route.CurrentZone,
            record.Snapshot.Route.DestinationZone,
            record.Snapshot.Route.ExpectedArrivalAt)));
    }

    private static string GroupKey(NCGroupId groupId) => groupId.Value.ToString("D");
    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private static NCCityGroupMutationResult Reject(
        NCGroupId groupId,
        NCCityGroupMutationStatus status,
        string error)
    {
        return new NCCityGroupMutationResult(status, groupId, [], null, error);
    }

    private sealed record GroupWrite(
        NCCityGroupSnapshot Snapshot,
        long ExpectedVersion,
        Guid EventId,
        string EventType);

    private sealed record MembershipWrite(
        NCCityActorGroupMembershipSnapshot Snapshot,
        long ExpectedVersion,
        Guid EventId,
        string EventType);

    private sealed record NCCityGroupEventPayload(
        NCGroupId GroupId,
        string EventType,
        string AuditSource,
        NCCityGroupSnapshot Snapshot);

    private sealed record NCCityMembershipEventPayload(
        NCActorId ActorId,
        NCGroupId GroupId,
        string EventType,
        string AuditSource,
        NCCityActorGroupMembershipSnapshot Snapshot);

    private sealed record NCCityGroupReceiptRecord(NCCityGroupSnapshot Snapshot, long AggregateVersion);
    private sealed record NCCityMembershipReceiptRecord(
        NCCityActorGroupMembershipSnapshot Snapshot,
        long AggregateVersion);
    private sealed record NCCityGroupBatchReceiptPayload(
        NCCityGroupReceiptRecord[] Groups,
        NCCityMembershipReceiptRecord? Membership,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
