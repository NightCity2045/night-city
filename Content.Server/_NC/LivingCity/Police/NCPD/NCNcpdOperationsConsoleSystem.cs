// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Database;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared.Access.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Secured stationary dispatch console. Read access comes from the physical terminal; command authority comes only
/// from the user's persistent active NCPD position and is revalidated for every mutation.
/// </summary>
public sealed partial class NCNcpdOperationsConsoleSystem : EntitySystem
{
    private static readonly ProtoId<DepartmentPrototype> NcpdDepartment = "NCPD";

    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCNcpdUnitSystem _units = default!;
    [Dependency] private NCNcpdCommandSystem _commands = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid User), OperationsView> _views = [];
    private readonly List<NCNcpdUnitSnapshot> _unitBuffer = [];
    private readonly List<NCNcpdCommandRecord> _commandBuffer = [];
    private readonly List<NCZoneInfo> _zoneBuffer = [];
    private readonly List<NCZoneGeometryInfo> _geometryBuffer = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdOperationsConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCNcpdOperationsConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCNcpdOperationsConsoleComponent, NCNcpdOperationsRefreshMessage>(OnRefresh);
        SubscribeLocalEvent<NCNcpdOperationsConsoleComponent, NCNcpdOperationsIssueMessage>(OnIssue);
        SubscribeLocalEvent<NCNcpdOperationsConsoleComponent, NCNcpdOperationsCancelMessage>(OnCancel);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _views.Clear());
    }

    private async void OnUiOpened(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user || !_accessReader.IsAllowed(user, uid))
        {
            if (args.Actor is { Valid: true } denied)
                _popup.PopupEntity(Loc.GetString("nc-ncpd-operations-access-denied"), uid, denied);
            return;
        }

        var view = new OperationsView();
        _views[(uid, user)] = view;
        view.Actor = await GetActorContextAsync(user);
        if (!_views.ContainsKey((uid, user)))
            return;
        await SendViewAsync(uid, component, user, view);
    }

    private void OnUiClosed(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        BoundUIClosedEvent args)
    {
        if (args.Actor is { Valid: true } user)
            _views.Remove((uid, user));
    }

    private async void OnRefresh(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        NCNcpdOperationsRefreshMessage args)
    {
        if (TryGetView(uid, args.Actor, out var user, out var view))
            await SendViewAsync(uid, component, user, view);
    }

    private async void OnIssue(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        NCNcpdOperationsIssueMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !_prototypes.TryIndex(component.Policy, out NCNcpdCommandPolicyPrototype? policy) || policy == null)
            return;

        view.Actor = await GetActorContextAsync(user);
        if (view.Actor == null || !TryGetAuthority(view.Actor.Job, out var authority))
            view.Notice = "Your active NCPD position has no operational command authority.";
        else if (_commands.TryIssue(
                     view.Actor,
                     authority,
                     policy,
                     new NCNcpdUnitId(args.UnitId),
                     args.Kind,
                     new NCZoneId(args.TargetZone),
                     args.Priority,
                     args.Reason,
                     out var error))
            view.Notice = "Order accepted by the authoritative dispatch board.";
        else
            view.Notice = error;

        await SendViewAsync(uid, component, user, view);
    }

    private async void OnCancel(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        NCNcpdOperationsCancelMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !_prototypes.TryIndex(component.Policy, out NCNcpdCommandPolicyPrototype? policy) || policy == null)
            return;

        view.Actor = await GetActorContextAsync(user);
        if (view.Actor == null || !TryGetAuthority(view.Actor.Job, out var authority))
            view.Notice = "Your active NCPD position has no operational command authority.";
        else if (_commands.TryCancel(
                     view.Actor,
                     authority,
                     policy,
                     new NCNcpdCommandId(args.CommandId),
                     args.Reason,
                     out var error))
            view.Notice = "Order cancelled; the unit is available for reassignment.";
        else
            view.Notice = error;

        await SendViewAsync(uid, component, user, view);
    }

    private async Task SendViewAsync(
        EntityUid uid,
        NCNcpdOperationsConsoleComponent component,
        EntityUid user,
        OperationsView view)
    {
        if (Deleted(uid) || Deleted(user) || !_views.ContainsKey((uid, user)) ||
            !_prototypes.TryIndex(component.Policy, out NCNcpdCommandPolicyPrototype? policy) || policy == null)
            return;

        NCNcpdCommandAuthorityPrototype? authority = null;
        if (view.Actor != null && TryGetAuthority(view.Actor.Job, out var resolvedAuthority))
            authority = resolvedAuthority;
        var hasAuthority = authority != null;
        _units.GetSnapshots(_unitBuffer);
        _commands.GetCommands(_commandBuffer);
        var sendStaticCatalog = !view.StaticCatalogSent;
        var zones = sendStaticCatalog
            ? BuildZones(uid, hasAuthority ? authority : null, policy.MaximumVisibleZones)
            : [];
        var units = new List<NCNcpdOperationsUnitSummary>(_unitBuffer.Count);
        foreach (var unit in _unitBuffer)
        {
            var callsign = Callsign(unit.UnitId);
            var leader = ActorName(unit.LeaderActorId);
            var members = string.Join(", ", unit.Members.Select(ActorName));
            var currentZone = ActorZoneName(unit.LeaderActorId);
            var assignment = _commands.TryGetActive(unit.UnitId, out var active)
                ? $"{active.Kind} // {ZoneName(active.TargetZone)} // {active.Status}"
                : "Available for dispatch";
            var commandable = hasAuthority && view.Actor != null &&
                              _commands.CanCommandUnit(view.Actor, authority!, unit.UnitId);
            units.Add(new NCNcpdOperationsUnitSummary(
                unit.UnitId.Value,
                callsign,
                leader,
                members,
                currentZone,
                assignment,
                commandable));
        }

        var commands = new List<NCNcpdOperationsCommandSummary>(_commandBuffer.Count);
        foreach (var command in _commandBuffer)
        {
            var cancellable = hasAuthority && view.Actor != null &&
                              command.Status is (NCNcpdCommandStatus.Accepted or NCNcpdCommandStatus.EnRoute or
                                  NCNcpdCommandStatus.Executing) &&
                              (command.IssuerCharacterId == view.Actor.CharacterId || authority!.CanCancelOthers) &&
                              _commands.CanCommandUnit(view.Actor, authority!, command.UnitId);
            commands.Add(new NCNcpdOperationsCommandSummary(
                command.CommandId.Value,
                command.UnitId.Value,
                Callsign(command.UnitId),
                command.Kind,
                ZoneName(command.TargetZone),
                command.Priority,
                command.IssuerName,
                command.Reason,
                command.Status,
                command.IssuedAt.Elapsed,
                command.Note,
                cancellable));
        }

        var map = sendStaticCatalog ? BuildMap(uid) : new MapSnapshot(string.Empty, 0, default, []);
        view.StaticCatalogSent = true;
        _ui.ServerSendUiMessage(uid, NCNcpdOperationsUiKey.Key, new NCNcpdOperationsUpdateMessage(
            view.Actor?.Job.Id ?? string.Empty,
            view.Actor?.Name ?? "No active NCPD employment",
            hasAuthority,
            view.Notice,
            hasAuthority ? authority!.AllowedCommands.ToList() : [],
            units,
            zones,
            commands,
            map.NetworkId,
            map.Z,
            map.Position,
            map.Features), user);
        view.Notice = string.Empty;
        await Task.CompletedTask;
    }

    private List<NCNcpdOperationsZoneSummary> BuildZones(
        EntityUid console,
        NCNcpdCommandAuthorityPrototype? authority,
        int maximum)
    {
        _zoneBuffer.Clear();
        if (_coordinates.TryGetCoordinates(console, out var coordinates))
            _zones.GetZones(coordinates.NetworkId, _zoneBuffer);
        var names = _zoneBuffer.ToDictionary(static zone => zone.Id, static zone => zone.Name);
        return _zoneBuffer
            .Where(zone => authority == null || authority.AllowedZoneSlots.Contains(zone.ContextSlot))
            .OrderBy(static zone => zone.ContextSlot)
            .ThenBy(static zone => zone.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maximum))
            .Select(zone => new NCNcpdOperationsZoneSummary(
                zone.Id.Value,
                zone.Name,
                names.GetValueOrDefault(zone.Parent, "City"),
                zone.ContextSlot.ToString()))
            .ToList();
    }

    private MapSnapshot BuildMap(EntityUid console)
    {
        if (!_coordinates.TryGetCoordinates(console, out var coordinates))
            return new MapSnapshot(string.Empty, 0, default, []);
        _zones.GetZones(coordinates.NetworkId, _zoneBuffer);
        var features = new List<NCAgentMapFeature>();
        foreach (var zone in _zoneBuffer)
        {
            if (!TryGetMapKind(zone.ContextSlot, out var featureKind) || !_zones.GetGeometry(zone.Id, _geometryBuffer))
                continue;
            foreach (var geometry in _geometryBuffer)
            {
                if (geometry.Kind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume &&
                    (geometry.Global || geometry.Kind == NCZoneGeometryKind.Polygon && geometry.Z == coordinates.Z ||
                     geometry.Kind == NCZoneGeometryKind.Volume && coordinates.Z >= geometry.MinZ &&
                     coordinates.Z <= geometry.MaxZ) && geometry.Vertices.Count >= 3)
                {
                    features.Add(new NCAgentMapFeature(featureKind, NCAgentMapGeometryKind.Polygon, zone.Name,
                        geometry.Vertices.ToList(), []));
                }
                else if (geometry.Kind == NCZoneGeometryKind.TileMask)
                {
                    var chunks = geometry.Chunks
                        .Where(chunk => chunk.Z == coordinates.Z)
                        .Select(chunk => new NCAgentMapTileChunk(chunk.Origin.X, chunk.Origin.Y, chunk.Rows.ToList()))
                        .ToList();
                    if (chunks.Count > 0)
                        features.Add(new NCAgentMapFeature(featureKind, NCAgentMapGeometryKind.TileMask, zone.Name, [], chunks));
                }
            }
        }
        return new MapSnapshot(coordinates.NetworkId.ToString(), coordinates.Z, coordinates.Position, features);
    }

    private async Task<NCNcpdIssuerContext?> GetActorContextAsync(EntityUid user)
    {
        if (!_prototypes.TryIndex(NcpdDepartment, out DepartmentPrototype? department) || department == null ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId))
            return null;
        var activeJob = await _database.GetNCActiveJobAsync(characterId);
        if (activeJob == null)
            return null;
        var job = new ProtoId<JobPrototype>(activeJob);
        if (!department.Roles.Contains(job) || !department.NCPositionRanks.TryGetValue(job, out var rank))
            return null;
        var name = _identity.GetIdentityShortInfo(user, user) ?? MetaData(user).EntityName;
        return new NCNcpdIssuerContext(characterId, job, name, rank);
    }

    private bool TryGetAuthority(
        ProtoId<JobPrototype> job,
        out NCNcpdCommandAuthorityPrototype authority)
    {
        authority = _prototypes.EnumeratePrototypes<NCNcpdCommandAuthorityPrototype>()
            .FirstOrDefault(profile => profile.Job == job)!;
        return authority != null;
    }

    private bool TryGetView(
        EntityUid console,
        EntityUid? actor,
        out EntityUid user,
        out OperationsView view)
    {
        user = actor ?? default;
        view = default!;
        if (!user.Valid || !_views.TryGetValue((console, user), out var found))
            return false;
        view = found;
        return true;
    }

    private string ActorName(Content.Shared._NC.Persistence.Identity.NCActorId actorId)
    {
        return _actors.TryGetCached(actorId, out var actor)
            ? actor.Snapshot.Identity.FullName
            : $"Officer {actorId.Value.ToString("N")[..6].ToUpperInvariant()}";
    }

    private string ActorZoneName(Content.Shared._NC.Persistence.Identity.NCActorId actorId)
    {
        return _actors.TryGetCached(actorId, out var actor)
            ? ZoneName(actor.Snapshot.Location.CurrentZone)
            : "Location unavailable";
    }

    private string ZoneName(NCZoneId zoneId) =>
        _zones.TryGetZone(zoneId, out var zone) ? zone.Name : "Unassigned";

    private static string Callsign(NCNcpdUnitId unitId) =>
        $"UNIT-{unitId.Value.ToString("N")[..4].ToUpperInvariant()}";

    private static bool TryGetMapKind(NCZoneContextSlot slot, out NCAgentMapFeatureKind kind)
    {
        kind = slot switch
        {
            NCZoneContextSlot.District => NCAgentMapFeatureKind.District,
            NCZoneContextSlot.Sector => NCAgentMapFeatureKind.Sector,
            NCZoneContextSlot.Street => NCAgentMapFeatureKind.Street,
            NCZoneContextSlot.Building => NCAgentMapFeatureKind.Building,
            _ => default,
        };
        return slot is NCZoneContextSlot.District or NCZoneContextSlot.Sector or NCZoneContextSlot.Street or
            NCZoneContextSlot.Building;
    }

    private sealed class OperationsView
    {
        public NCNcpdIssuerContext? Actor;
        public bool StaticCatalogSent;
        public string Notice = string.Empty;
    }

    private sealed record MapSnapshot(string NetworkId, int Z, Vector2 Position, List<NCAgentMapFeature> Features);
}
