// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.City.Atlas;
using Content.Server._NC.Dispatch;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Police.Components;
using Content.Shared._NC.Police.UI;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles.Jobs;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._NC.Police;

/// <summary>
/// NCPD city monitoring console. Every person in the city is a dot on the console's map; the police network
/// refreshes a person's position as often as their district's coverage allows, and a person in a district without
/// coverage is not shown at all. Picking a dot shows the name and job. Open NCPD incidents are shown too.
/// </summary>
public sealed partial class NCPoliceMonitorSystem : EntitySystem
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private SharedJobSystem _jobs = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;
    [Dependency] private NCPoliceCoverageSystem _coverage = default!;
    [Dependency] private NCDispatchSystem _dispatch = default!;

    private readonly Dictionary<EntityUid, Sighting> _sightings = new();
    private readonly HashSet<EntityUid> _seen = new();
    private readonly List<EntityUid> _gone = new();
    private TimeSpan _nextUpdate;

    private readonly record struct Sighting(NCMapCoordinates At, TimeSpan Time);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPoliceMonitorConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _sightings.Clear());
    }

    private void OnOpened(Entity<NCPoliceMonitorConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!NCPoliceMonitorUiKey.Key.Equals(args.UiKey))
            return;

        UpdateSightings(_timing.CurTime);
        _ui.ServerSendUiMessage(ent.Owner, NCPoliceMonitorUiKey.Key, new NCPoliceMonitorMapMessage(BuildSnapshot(ent)), args.Actor);
        SendContacts(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + UpdateInterval;
        var updated = false;
        var query = EntityQueryEnumerator<NCPoliceMonitorConsoleComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!_ui.IsUiOpen(uid, NCPoliceMonitorUiKey.Key))
                continue;

            if (!updated)
            {
                UpdateSightings(now);
                updated = true;
            }

            SendContacts(uid);
        }
    }

    /// <summary>
    /// Refreshes the position of everyone whose district refresh has passed and forgets everyone the police
    /// network no longer sees.
    /// </summary>
    public void UpdateSightings(TimeSpan now)
    {
        _seen.Clear();
        // People are bodies a mind plays; NPCs are not on the police network.
        var query = EntityQueryEnumerator<MindContainerComponent>();
        while (query.MoveNext(out var uid, out var mind))
        {
            if (!mind.HasMind || _mobState.IsDead(uid) ||
                !_mapCoordinates.TryGetCoordinates(uid, out var at) ||
                !_coverage.TryGetRefresh(at, out var refresh))
            {
                continue;
            }

            _seen.Add(uid);
            if (!_sightings.TryGetValue(uid, out var last) || now - last.Time >= refresh)
                _sightings[uid] = new Sighting(at, now);
        }

        _gone.Clear();
        foreach (var uid in _sightings.Keys)
        {
            if (!_seen.Contains(uid))
                _gone.Add(uid);
        }

        foreach (var uid in _gone)
            _sightings.Remove(uid);
    }

    /// <summary>The people a console on this city network shows now.</summary>
    public List<NCCityMapContact> GetContacts(NCZNetworkId networkId)
    {
        var contacts = new List<NCCityMapContact>();
        foreach (var (uid, sighting) in _sightings)
        {
            if (sighting.At.NetworkId != networkId || TerminatingOrDeleted(uid))
                continue;

            var job = _minds.TryGetMind(uid, out var mindId, out _) && _jobs.MindTryGetJobName(mindId, out var name)
                ? name
                : Loc.GetString("nc-police-monitor-no-job");

            contacts.Add(new NCCityMapContact(
                GetNetEntity(uid).Id,
                Name(uid),
                job,
                sighting.At.NetworkId.ToString(),
                sighting.At.Z,
                sighting.At.Position));
        }

        return contacts;
    }

    private void SendContacts(EntityUid console)
    {
        if (!_mapCoordinates.TryGetCoordinates(console, out var coordinates))
            return;

        var incidents = new List<NCCityMapMarker>();
        _dispatch.AddMarkers(NCDispatchSystem.PoliceService, incidents);
        _ui.ServerSendUiMessage(console, NCPoliceMonitorUiKey.Key,
            new NCPoliceMonitorContactsMessage(GetContacts(coordinates.NetworkId), incidents));
    }

    private NCCityMapSnapshotEvent BuildSnapshot(EntityUid console)
    {
        if (!_mapCoordinates.TryGetCoordinates(console, out var coordinates) || Transform(console).MapUid is not { } map)
            return new NCCityMapSnapshotEvent(string.Empty, 0, default, [], [], Loc.GetString("nc-city-map-no-signal"));

        var features = new List<NCCityMapFeature>();
        _atlas.BuildFeatures(coordinates.NetworkId, coordinates.Z, map, features);
        var incidents = new List<NCCityMapMarker>();
        _dispatch.AddMarkers(NCDispatchSystem.PoliceService, incidents);
        return new NCCityMapSnapshotEvent(
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            features,
            incidents,
            features.Count == 0 ? Loc.GetString("nc-city-map-grid-only") : string.Empty);
    }
}
