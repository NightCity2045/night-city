// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Lazily validates the current derived city topology. Graph changes only mark the report dirty; the potentially
/// city-wide audit runs on an explicit admin/test/deployment-boundary request, never from Update().
/// </summary>
public sealed partial class NCCityConnectivityValidationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawns = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly List<NCCityConnectivityValidationIssue> _cachedIssues = [];
    private readonly List<NCZoneInfo> _zoneBuffer = [];
    private readonly List<NCRoomNodeInfo> _roomBuffer = [];
    private readonly List<NCCityPortalInfo> _portalBuffer = [];
    private readonly List<NCCitySpawnCandidateInfo> _candidateBuffer = [];
    private bool _dirty = true;
    private long _validationVersion;
    private NCCityConnectivityValidationSummary _summary;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<NCCityConnectivityChangedEvent>(OnCityGraphChanged);
        SubscribeLocalEvent<NCRoomPortalGraphChangedEvent>(OnRoomGraphChanged);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnIndexChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public NCCityConnectivityValidationSummary Validate(List<NCCityConnectivityValidationIssue> result)
    {
        if (_dirty)
            RebuildReport();
        result.Clear();
        result.AddRange(_cachedIssues);
        return _summary;
    }

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args) => _dirty = true;
    private void OnCityGraphChanged(ref NCCityConnectivityChangedEvent args) => _dirty = true;
    private void OnRoomGraphChanged(ref NCRoomPortalGraphChangedEvent args) => _dirty = true;
    private void OnSpawnIndexChanged(ref NCCitySpawnCandidateIndexChangedEvent args) => _dirty = true;

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCCityConnectivityValidationPrototype>() ||
            args.WasModified<NCCitySpawnCandidateProfilePrototype>())
        {
            _dirty = true;
        }
    }

    private void RebuildReport()
    {
        _cachedIssues.Clear();
        if (!TryGetRules(out var rules))
        {
            Add(
                NCCityConnectivityIssueSeverity.Error,
                NCCityConnectivityIssueCode.InvalidValidationRules,
                default,
                "Exactly one valid ncCityConnectivityValidation prototype is required.");
            FinishReport();
            return;
        }

        _zones.GetZones(_zoneBuffer);
        var zones = _zoneBuffer.ToDictionary(static zone => zone.Id);
        var reverse = zones.Keys.ToDictionary(static zone => zone, static _ => new HashSet<NCZoneId>());
        var enabledOutgoing = zones.Keys.ToDictionary(static zone => zone, static _ => 0);

        foreach (var zone in _zoneBuffer)
        {
            _city.GetOutgoingPortals(zone.Id, _portalBuffer);
            foreach (var portal in _portalBuffer)
            {
                ValidatePortal(portal, zones);
                if (!portal.Enabled || portal.ToZone == portal.FromZone || !zones.ContainsKey(portal.ToZone))
                    continue;
                enabledOutgoing[portal.FromZone]++;
                reverse[portal.ToZone].Add(portal.FromZone);
            }
        }

        var canReachStreet = BuildStreetReachability(reverse);
        foreach (var zone in _zoneBuffer)
        {
            _rooms.GetRooms(zone.Id, _roomBuffer);
            var hasRoom = _roomBuffer.Count != 0;
            if (rules.RequiresNavigationRoom.Contains(zone.ContextSlot) && !hasRoom)
            {
                Add(
                    NCCityConnectivityIssueSeverity.Error,
                    NCCityConnectivityIssueCode.MissingNavigationRoom,
                    zone.Id,
                    $"{zone.ContextSlot} '{zone.Name}' has no derived navigation room.");
            }

            if (rules.RequiresTraversableExit.Contains(zone.ContextSlot) &&
                enabledOutgoing.GetValueOrDefault(zone.Id) == 0)
            {
                var code = zone.ContextSlot == NCZoneContextSlot.Street
                    ? NCCityConnectivityIssueCode.IsolatedStreet
                    : NCCityConnectivityIssueCode.NoTraversableExit;
                Add(
                    NCCityConnectivityIssueSeverity.Error,
                    code,
                    zone.Id,
                    $"{zone.ContextSlot} '{zone.Name}' has no enabled exit to another semantic zone.");
            }

            if (rules.RequiresStreetReachability.Contains(zone.ContextSlot) && !canReachStreet.Contains(zone.Id))
            {
                Add(
                    NCCityConnectivityIssueSeverity.Error,
                    NCCityConnectivityIssueCode.UnreachableFromStreet,
                    zone.Id,
                    $"{zone.ContextSlot} '{zone.Name}' cannot reach any Street through enabled directed portals.");
            }

            if (!rules.RequiredSpawnProfiles.TryGetValue(zone.ContextSlot, out var requiredProfiles) || !hasRoom)
                continue;
            foreach (var profile in requiredProfiles)
            {
                _spawns.GetCandidates(profile, zone.Id, _candidateBuffer);
                if (_candidateBuffer.Count == 0)
                {
                    Add(
                        NCCityConnectivityIssueSeverity.Warning,
                        NCCityConnectivityIssueCode.MissingSpawnCandidate,
                        zone.Id,
                        $"{zone.ContextSlot} '{zone.Name}' has no candidate for profile {profile}.");
                }
            }
        }

        FinishReport();
    }

    private void ValidatePortal(
        NCCityPortalInfo portal,
        IReadOnlyDictionary<NCZoneId, NCZoneInfo> zones)
    {
        if (!zones.ContainsKey(portal.FromZone) || !zones.ContainsKey(portal.ToZone))
        {
            Add(
                NCCityConnectivityIssueSeverity.Error,
                NCCityConnectivityIssueCode.InvalidPortalEndpoint,
                portal.FromZone,
                $"Portal {portal.Id} references an unknown endpoint.");
            return;
        }

        if (!_zones.TryGetZoneNetwork(portal.FromZone, out var fromNetwork) ||
            !_zones.TryGetZoneNetwork(portal.ToZone, out var toNetwork) ||
            fromNetwork != toNetwork ||
            portal.EntryTarget.NetworkId != fromNetwork ||
            portal.ExitTarget.NetworkId != toNetwork)
        {
            Add(
                NCCityConnectivityIssueSeverity.Error,
                NCCityConnectivityIssueCode.CrossNetworkPortal,
                portal.FromZone,
                $"Portal {portal.Id} crosses or mislabels a Z-network boundary.");
        }

        if (!float.IsFinite(portal.BaseTravelSeconds) || portal.BaseTravelSeconds <= 0f ||
            !float.IsFinite(portal.CapacityPerMinute) || portal.CapacityPerMinute <= 0f ||
            !float.IsFinite(portal.ThreatCost) || portal.ThreatCost < 0f)
        {
            Add(
                NCCityConnectivityIssueSeverity.Error,
                NCCityConnectivityIssueCode.InvalidPortalCost,
                portal.FromZone,
                $"Portal {portal.Id} has invalid travel cost, capacity, or threat.");
        }

        if (portal.StableId.IsValid &&
            portal.Kind is not (NCCityPortalKind.Vertical or NCCityPortalKind.DeploymentEntrance))
        {
            Add(
                NCCityConnectivityIssueSeverity.Error,
                NCCityConnectivityIssueCode.InvalidStablePortalKind,
                portal.FromZone,
                $"Runtime {portal.Kind} portal {portal.Id} illegally carries stable identity {portal.StableId}.");
        }
    }

    private HashSet<NCZoneId> BuildStreetReachability(
        IReadOnlyDictionary<NCZoneId, HashSet<NCZoneId>> reverse)
    {
        var reachable = new HashSet<NCZoneId>();
        var frontier = new Queue<NCZoneId>();
        foreach (var street in _zoneBuffer.Where(static zone => zone.ContextSlot == NCZoneContextSlot.Street))
        {
            reachable.Add(street.Id);
            frontier.Enqueue(street.Id);
        }

        while (frontier.TryDequeue(out var current))
        {
            foreach (var predecessor in reverse[current])
            {
                if (reachable.Add(predecessor))
                    frontier.Enqueue(predecessor);
            }
        }

        return reachable;
    }

    private bool TryGetRules(out NCCityConnectivityValidationPrototype rules)
    {
        var loaded = _prototypes.EnumeratePrototypes<NCCityConnectivityValidationPrototype>().ToArray();
        if (loaded.Length == 1 &&
            loaded[0].RequiresNavigationRoom.Count != 0 &&
            loaded[0].RequiresTraversableExit.Count != 0)
        {
            rules = loaded[0];
            return true;
        }

        rules = default!;
        return false;
    }

    private void Add(
        NCCityConnectivityIssueSeverity severity,
        NCCityConnectivityIssueCode code,
        NCZoneId zoneId,
        string message)
    {
        _cachedIssues.Add(new NCCityConnectivityValidationIssue(severity, code, zoneId, message));
    }

    private void FinishReport()
    {
        _cachedIssues.Sort(static (left, right) =>
        {
            var severity = right.Severity.CompareTo(left.Severity);
            if (severity != 0)
                return severity;
            var code = left.Code.CompareTo(right.Code);
            return code != 0 ? code : left.ZoneId.Value.CompareTo(right.ZoneId.Value);
        });
        _validationVersion = _validationVersion == long.MaxValue ? 1 : _validationVersion + 1;
        _summary = new NCCityConnectivityValidationSummary(
            _validationVersion,
            _city.GraphVersion,
            _rooms.GraphVersion,
            _spawns.IndexVersion,
            _cachedIssues.Count(static issue => issue.Severity == NCCityConnectivityIssueSeverity.Error),
            _cachedIssues.Count(static issue => issue.Severity == NCCityConnectivityIssueSeverity.Warning));
        _dirty = false;
    }
}
