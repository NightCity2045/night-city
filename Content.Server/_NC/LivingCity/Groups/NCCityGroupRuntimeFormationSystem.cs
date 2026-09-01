// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Groups.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Groups;

/// <summary>
/// Owns complete round-local group formations. A formation becomes visible only after every actor binding validates.
/// </summary>
public sealed partial class NCCityGroupRuntimeFormationSystem : EntitySystem
{
    [Dependency] private NCCityActorRuntimeIndexSystem _actors = default!;

    private readonly Dictionary<NCGroupId, NCCityRuntimeFormationState> _groups = [];
    private readonly Dictionary<NCActorId, NCGroupId> _groupByActor = [];

    public int MaterializedGroupCount => _groups.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnActorBindingChanged);
        SubscribeLocalEvent<NCCityGroupPersistedEvent>(OnGroupPersisted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public bool TryGetGroup(NCGroupId groupId, out NCCityRuntimeFormationState state) =>
        _groups.TryGetValue(groupId, out state!);

    public bool TryGetGroupForActor(NCActorId actorId, out NCGroupId groupId) =>
        _groupByActor.TryGetValue(actorId, out groupId);

    public bool TryApply(
        NCCityGroupRecord group,
        IReadOnlyDictionary<NCActorId, EntityUid> bindings,
        NCCityGroupPhysicalizationPolicyPrototype policy,
        out NCCityRuntimeFormationState? state,
        out string? error)
    {
        state = null;
        error = null;
        var snapshot = group.Snapshot;
        if (!snapshot.GroupId.IsValid || snapshot.Members.Length == 0 ||
            snapshot.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived ||
            !bindings.TryGetValue(snapshot.LeaderActorId, out var leaderEntity) || !Exists(leaderEntity))
        {
            error = "The persistent group cannot form without a live leader and active membership.";
            return false;
        }

        var orderedMembers = snapshot.Members
            .OrderBy(member => member.ActorId == snapshot.LeaderActorId ? 0 : 1)
            .ThenBy(static member => member.ActorId.Value)
            .ToArray();
        var validated = new List<(NCCityGroupMember Member, EntityUid Entity)>(orderedMembers.Length);
        foreach (var member in orderedMembers)
        {
            if (!bindings.TryGetValue(member.ActorId, out var entity) || !Exists(entity) ||
                !_actors.TryGetEntity(member.ActorId, out var bound) || bound != entity)
            {
                error = $"Actor {member.ActorId} has no matching physical binding.";
                return false;
            }
            if (_groupByActor.TryGetValue(member.ActorId, out var existing) && existing != snapshot.GroupId)
            {
                error = $"Actor {member.ActorId} already belongs to physical formation {existing}.";
                return false;
            }
            if (TryComp<NCCityGroupFormationComponent>(entity, out var component) &&
                component.GroupId.IsValid && component.GroupId != snapshot.GroupId)
            {
                error = $"Entity {entity} already carries another physical formation.";
                return false;
            }
            validated.Add((member, entity));
        }

        // Validation completes before the previous formation is removed, preventing a partially applied replacement.
        ClearGroup(snapshot.GroupId);
        var offsets = BuildOffsets(snapshot.FormationPolicy, validated.Count, policy);
        var runtimeMembers = new NCCityRuntimeFormationMember[validated.Count];
        for (var i = 0; i < validated.Count; i++)
        {
            var (member, entity) = validated[i];
            var component = EnsureComp<NCCityGroupFormationComponent>(entity);
            component.GroupId = snapshot.GroupId;
            component.SourceGroupVersion = group.AggregateVersion;
            component.LeaderActorId = snapshot.LeaderActorId;
            component.LeaderEntity = leaderEntity;
            component.Role = member.Role;
            component.SlotIndex = i;
            component.DesiredOffset = offsets[i];
            component.Policy = snapshot.FormationPolicy;
            component.IsLeader = member.ActorId == snapshot.LeaderActorId;
            runtimeMembers[i] = new NCCityRuntimeFormationMember(
                member.ActorId,
                entity,
                member.Role,
                i,
                offsets[i],
                component.IsLeader);
        }

        state = new NCCityRuntimeFormationState(
            snapshot.GroupId,
            group.AggregateVersion,
            snapshot.FormationPolicy,
            snapshot.LeaderActorId,
            leaderEntity,
            runtimeMembers);
        _groups.Add(snapshot.GroupId, state);
        foreach (var member in runtimeMembers)
            _groupByActor[member.ActorId] = snapshot.GroupId;
        return true;
    }

    public bool ClearGroup(NCGroupId groupId)
    {
        if (!_groups.Remove(groupId, out var state))
            return false;
        foreach (var member in state.Members)
        {
            _groupByActor.Remove(member.ActorId);
            if (Exists(member.EntityUid) &&
                TryComp<NCCityGroupFormationComponent>(member.EntityUid, out var component) &&
                component.GroupId == groupId)
            {
                RemComp<NCCityGroupFormationComponent>(member.EntityUid);
            }
        }
        return true;
    }

    private void OnActorBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound && _groupByActor.TryGetValue(args.ActorId, out var groupId))
            ClearGroup(groupId);
    }

    private void OnGroupPersisted(NCCityGroupPersistedEvent args)
    {
        if (_groups.TryGetValue(args.GroupId, out var state) && state.SourceGroupVersion < args.AggregateVersion)
            ClearGroup(args.GroupId);
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => Reset();

    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => Reset();

    private void Reset()
    {
        foreach (var groupId in _groups.Keys.ToArray())
            ClearGroup(groupId);
        _groups.Clear();
        _groupByActor.Clear();
    }

    private static Vector2[] BuildOffsets(
        NCCityGroupFormationPolicy formation,
        int count,
        NCCityGroupPhysicalizationPolicyPrototype policy)
    {
        var result = new Vector2[count];
        if (count <= 1)
            return result;

        var spacing = formation switch
        {
            NCCityGroupFormationPolicy.Column => policy.ColumnSpacing,
            NCCityGroupFormationPolicy.Wedge => policy.WedgeSpacing,
            NCCityGroupFormationPolicy.Defensive => policy.DefensiveSpacing,
            _ => policy.LooseSpacing,
        };
        for (var i = 1; i < count; i++)
        {
            result[i] = formation switch
            {
                NCCityGroupFormationPolicy.Column => new Vector2(0f, -spacing * i),
                NCCityGroupFormationPolicy.Wedge => BuildWedgeOffset(i, spacing),
                NCCityGroupFormationPolicy.Defensive => BuildRingOffset(i, count - 1, spacing),
                _ => BuildLooseOffset(i, spacing),
            };
        }
        return result;
    }

    private static Vector2 BuildWedgeOffset(int index, float spacing)
    {
        var row = (index + 1) / 2;
        var side = index % 2 == 0 ? 1f : -1f;
        return new Vector2(side * row * spacing, -row * spacing);
    }

    private static Vector2 BuildRingOffset(int index, int followerCount, float spacing)
    {
        var angle = MathF.Tau * (index - 1) / Math.Max(1, followerCount);
        var radius = Math.Max(spacing, spacing * followerCount / MathF.Tau);
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    private static Vector2 BuildLooseOffset(int index, float spacing)
    {
        const float goldenAngle = 2.3999632f;
        var radius = spacing * MathF.Sqrt(index);
        var angle = goldenAngle * index;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }
}
