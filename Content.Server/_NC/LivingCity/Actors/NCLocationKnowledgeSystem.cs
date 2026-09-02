// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Round-scoped semantic place knowledge learned from the actor's real physical history.
/// </summary>
public sealed partial class NCLocationKnowledgeSystem : EntitySystem
{
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly Dictionary<NCActorId, HashSet<NCZoneId>> _known = [];
    private readonly object _lock = new();

    public int ActorCount
    {
        get { lock (_lock) return _known.Count; }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    public void Seed(NCCityActorRecord actor)
    {
        Learn(actor.Snapshot.Identity.ActorId, actor.Snapshot.Location.CurrentZone);
    }

    public void GetKnown(NCActorId actorId, HashSet<NCZoneId> sink)
    {
        sink.Clear();
        lock (_lock)
        {
            if (_known.TryGetValue(actorId, out var known))
                sink.UnionWith(known);
        }
    }

    public int CountKnown(NCActorId actorId)
    {
        lock (_lock)
            return _known.TryGetValue(actorId, out var known) ? known.Count : 0;
    }

    public void Learn(NCActorId actorId, NCZoneId zoneId)
    {
        if (!actorId.IsValid || !zoneId.IsValid)
            return;
        var chain = new List<NCZoneId>();
        var visited = new HashSet<NCZoneId>();
        var current = zoneId;
        while (current.IsValid && visited.Add(current) && _zones.TryGetZone(current, out var zone))
        {
            chain.Add(current);
            current = zone.Parent;
        }
        if (chain.Count == 0)
            return;
        lock (_lock)
        {
            if (!_known.TryGetValue(actorId, out var known))
            {
                known = [];
                _known.Add(actorId, known);
            }
            known.UnionWith(chain);
        }
    }

    private void OnActorPersisted(NCCityActorPersistedEvent args) => Learn(args.ActorId, args.CurrentZone);

    private void Clear()
    {
        lock (_lock)
            _known.Clear();
    }
}
