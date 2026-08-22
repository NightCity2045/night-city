// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Produces bounded, immutable connectivity snapshots only for administrators who explicitly enable diagnostics.
/// It never exposes server EntityUid values and performs no work from Update().
/// </summary>
public sealed partial class NCCityConnectivityDebugSystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawns = default!;
    [Dependency] private NCCityConnectivityValidationSystem _validation = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly HashSet<NetUserId> _enabled = [];
    private readonly List<NCZoneInfo> _zoneBuffer = [];
    private readonly List<NCCityPortalInfo> _cityPortalBuffer = [];
    private readonly List<NCRoomNodeInfo> _roomBuffer = [];
    private readonly List<NCRoomPortalInfo> _roomPortalBuffer = [];
    private readonly List<NCCityConnectivityValidationIssue> _issueBuffer = [];
    private bool _refreshPending;
    private long _coalescedRefreshRequests;
    private long _sentSnapshots;

    public NCConnectivityDebugRuntimeCounters GetRuntimeCounters() => new(
        _enabled.Count,
        _refreshPending,
        _coalescedRefreshRequests,
        _sentSnapshots);

    public override void Initialize()
    {
        base.Initialize();
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
        SubscribeNetworkEvent<NCCityConnectivityDebugRequest>(OnRequest);
        SubscribeLocalEvent<NCCityConnectivityChangedEvent>(OnCityGraphChanged);
        SubscribeLocalEvent<NCRoomPortalGraphChangedEvent>(OnRoomGraphChanged);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnIndexChanged);
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        _enabled.Clear();
        _refreshPending = false;
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_refreshPending)
            return;

        // Several authoritative indexes can change during one structural transaction. Build and transmit only
        // the final snapshot on the next update, after every graph has reached the same topology generation.
        _refreshPending = false;
        RefreshSubscribers();
    }

    private void OnRequest(NCCityConnectivityDebugRequest request, EntitySessionEventArgs args)
    {
        if (!_admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping))
        {
            _enabled.Remove(args.SenderSession.UserId);
            RaiseNetworkEvent(EmptySnapshot(false), args.SenderSession);
            return;
        }

        if (!request.Enabled)
        {
            _enabled.Remove(args.SenderSession.UserId);
            RaiseNetworkEvent(EmptySnapshot(true), args.SenderSession);
            return;
        }

        _enabled.Add(args.SenderSession.UserId);
        SendSnapshot(args.SenderSession);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie)
            _enabled.Remove(args.Session.UserId);
    }

    private void OnCityGraphChanged(ref NCCityConnectivityChangedEvent args) => QueueRefresh();
    private void OnRoomGraphChanged(ref NCRoomPortalGraphChangedEvent args) => QueueRefresh();
    private void OnSpawnIndexChanged(ref NCCitySpawnCandidateIndexChangedEvent args) => QueueRefresh();

    private void QueueRefresh()
    {
        if (_enabled.Count == 0)
            return;

        if (_refreshPending)
            _coalescedRefreshRequests++;
        _refreshPending = true;
    }

    private void RefreshSubscribers()
    {
        if (_enabled.Count == 0)
            return;

        foreach (var session in _players.Sessions.ToArray())
        {
            if (!_enabled.Contains(session.UserId))
                continue;
            if (!_admins.HasAdminFlag(session, AdminFlags.Mapping))
            {
                _enabled.Remove(session.UserId);
                RaiseNetworkEvent(EmptySnapshot(false), session);
                continue;
            }

            SendSnapshot(session);
        }
    }

    private void SendSnapshot(ICommonSession session)
    {
        _sentSnapshots++;
        _zones.GetZones(_zoneBuffer);
        _rooms.GetRooms(_roomBuffer);
        _rooms.GetPortals(_roomPortalBuffer);
        var validation = _validation.Validate(_issueBuffer);

        var zones = _zoneBuffer
            .Select(static zone => new NCConnectivityDebugZone(
                zone.Id,
                zone.Parent,
                zone.Name,
                zone.ContextSlot))
            .ToArray();

        var cityPortals = new List<NCConnectivityDebugCityPortal>();
        foreach (var zone in _zoneBuffer)
        {
            _city.GetOutgoingPortals(zone.Id, _cityPortalBuffer);
            foreach (var portal in _cityPortalBuffer)
            {
                cityPortals.Add(new NCConnectivityDebugCityPortal(
                    portal.Id.Value,
                    portal.FromZone,
                    portal.ToZone,
                    portal.EntryTarget,
                    portal.ExitTarget,
                    portal.Kind,
                    portal.BaseTravelSeconds,
                    portal.ThreatCost,
                    portal.Enabled,
                    portal.StableId.IsValid));
            }
        }

        cityPortals.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        RaiseNetworkEvent(new NCCityConnectivityDebugSnapshot(
            true,
            true,
            _city.GraphVersion,
            _rooms.GraphVersion,
            _spawns.IndexVersion,
            validation.ValidationVersion,
            zones,
            _roomBuffer.Select(static room => new NCConnectivityDebugRoom(
                room.Id.Value,
                room.ZoneId,
                room.Anchor,
                room.NavigationNodeCount)).ToArray(),
            cityPortals.ToArray(),
            _roomPortalBuffer.Select(static portal => new NCConnectivityDebugRoomPortal(
                portal.Id.Value,
                portal.RoomA.Value,
                portal.RoomB.Value,
                portal.TargetA,
                portal.TargetB,
                portal.Kind == NCRoomPortalKind.Door
                    ? NCConnectivityDebugRoomPortalKind.Door
                    : NCConnectivityDebugRoomPortalKind.OpenBoundary,
                portal.Controller.IsValid())).ToArray(),
            _issueBuffer.Select(static issue => new NCConnectivityDebugIssue(
                issue.Severity == NCCityConnectivityIssueSeverity.Error
                    ? NCConnectivityDebugIssueSeverity.Error
                    : NCConnectivityDebugIssueSeverity.Warning,
                (byte) issue.Code,
                issue.ZoneId,
                issue.Message)).ToArray()), session);
    }

    private NCCityConnectivityDebugSnapshot EmptySnapshot(bool authorized)
    {
        return new NCCityConnectivityDebugSnapshot(
            false,
            authorized,
            _city.GraphVersion,
            _rooms.GraphVersion,
            _spawns.IndexVersion,
            0,
            [],
            [],
            [],
            [],
            []);
    }
}

public readonly record struct NCConnectivityDebugRuntimeCounters(
    int Subscribers,
    bool RefreshPending,
    long CoalescedRefreshRequests,
    long SentSnapshots);
