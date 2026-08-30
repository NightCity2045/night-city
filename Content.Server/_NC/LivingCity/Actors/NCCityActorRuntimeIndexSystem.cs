// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Enforces the current-epoch one-to-one ActorId/EntityUid materialization invariant.
/// The index is bounded by materialized actors and is cleared whenever the live round ends.
/// </summary>
public sealed partial class NCCityActorRuntimeIndexSystem : EntitySystem
{
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCActorId, EntityUid> _byActor = [];
    private readonly Dictionary<EntityUid, NCActorId> _byEntity = [];

    public int MaterializedActorCount => _byActor.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorBindingComponent, ComponentStartup>(OnBindingStartup);
        SubscribeLocalEvent<NCCityActorBindingComponent, ComponentShutdown>(OnBindingShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    public bool TryBind(NCActorId actorId, EntityUid entityUid)
    {
        if (!_clock.IsReady || !actorId.IsValid || !Exists(entityUid))
            return false;

        if (_byActor.TryGetValue(actorId, out var actorEntity))
            return actorEntity == entityUid;
        if (_byEntity.TryGetValue(entityUid, out var entityActor))
            return entityActor == actorId;

        var component = EnsureComp<NCCityActorBindingComponent>(entityUid);
        if (component.ActorId.IsValid && component.ActorId != actorId)
            return false;

        component.ActorId = actorId;
        _byActor.Add(actorId, entityUid);
        _byEntity.Add(entityUid, actorId);
        RaiseLocalEvent(new NCCityActorMaterializationChangedEvent(actorId, entityUid, true));
        return true;
    }

    public bool Unbind(EntityUid entityUid)
    {
        if (!_byEntity.Remove(entityUid, out var actorId))
            return false;

        _byActor.Remove(actorId);
        RemComp<NCCityActorBindingComponent>(entityUid);
        RaiseLocalEvent(new NCCityActorMaterializationChangedEvent(actorId, entityUid, false));
        return true;
    }

    public bool TryGetEntity(NCActorId actorId, out EntityUid entityUid)
    {
        if (!_byActor.TryGetValue(actorId, out entityUid))
            return false;
        if (Exists(entityUid))
            return true;

        RemoveStaleBinding(actorId, entityUid);
        entityUid = default;
        return false;
    }

    public bool TryGetActor(EntityUid entityUid, out NCActorId actorId)
    {
        if (!_byEntity.TryGetValue(entityUid, out actorId))
            return false;
        if (Exists(entityUid))
            return true;

        RemoveStaleBinding(actorId, entityUid);
        actorId = default;
        return false;
    }

    public void GetBindings(List<(NCActorId ActorId, EntityUid EntityUid)> result)
    {
        result.Clear();
        foreach (var (actorId, entityUid) in _byActor.ToArray())
        {
            if (Exists(entityUid))
                result.Add((actorId, entityUid));
            else
                RemoveStaleBinding(actorId, entityUid);
        }
        result.Sort(static (left, right) => left.ActorId.Value.CompareTo(right.ActorId.Value));
    }

    private void OnBindingStartup(
        Entity<NCCityActorBindingComponent> entity,
        ref ComponentStartup args)
    {
        var actorId = entity.Comp.ActorId;
        if (!actorId.IsValid || !_clock.IsReady)
            return;
        if (_byActor.ContainsKey(actorId) || _byEntity.ContainsKey(entity.Owner))
        {
            Log.Error($"Rejected duplicate persistent actor binding {actorId} on entity {ToPrettyString(entity.Owner)}.");
            RemCompDeferred<NCCityActorBindingComponent>(entity.Owner);
            return;
        }

        _byActor.Add(actorId, entity.Owner);
        _byEntity.Add(entity.Owner, actorId);
        RaiseLocalEvent(new NCCityActorMaterializationChangedEvent(actorId, entity.Owner, true));
    }

    private void OnBindingShutdown(
        Entity<NCCityActorBindingComponent> entity,
        ref ComponentShutdown args)
    {
        if (!_byEntity.Remove(entity.Owner, out var actorId))
            return;
        _byActor.Remove(actorId);
        RaiseLocalEvent(new NCCityActorMaterializationChangedEvent(actorId, entity.Owner, false));
    }

    private void RemoveStaleBinding(NCActorId actorId, EntityUid entityUid)
    {
        if (_byActor.TryGetValue(actorId, out var bound) && bound == entityUid)
            _byActor.Remove(actorId);
        if (_byEntity.TryGetValue(entityUid, out var reverse) && reverse == actorId)
            _byEntity.Remove(entityUid);
        RaiseLocalEvent(new NCCityActorMaterializationChangedEvent(actorId, entityUid, false));
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent args)
    {
        if (args.Old != GameRunLevel.InRound || args.New == GameRunLevel.InRound)
            return;

        // Only currently materialized bindings are visited; persistent actors without entities are untouched.
        foreach (var entityUid in _byEntity.Keys.ToArray())
        {
            if (Exists(entityUid))
                RemComp<NCCityActorBindingComponent>(entityUid);
        }

        _byActor.Clear();
        _byEntity.Clear();
    }
}
