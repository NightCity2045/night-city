// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Shared.Hands.Components;
using NightCity.Diagnostics.Contracts;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Content.Server.GameTicking;
using Content.Server.Mind;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Power.Components;
using Robust.Shared.Network;
using Robust.Shared.Maths;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    [Dependency] private MindSystem _minds = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private AccessReaderSystem _accessReaders = default!;
    [Dependency] private SharedAccessSystem _access = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private AtmosphereSystem _atmosphere = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedPointLightSystem _fixtureLights = default!;

    private sealed class Fixture(EntityUid map)
    {
        public EntityUid Map = map;
        public EntityUid? Grid;
        public readonly HashSet<EntityUid> Entities = [];
    }

    private readonly Dictionary<string, Fixture> _fixtures = new();

    private JsonObject ActionCatalogue() => new()
    {
        ["actions"] = JsonSerializer.SerializeToNode(_profile == DiagnosticProfile.Observe
            ? Array.Empty<string>() : new[] { "interact", "pickup", "drop", "use_in_hand" }),
        ["executionPath"] = "server-authoritative",
        ["actorPolicy"] = "attached-player-or-owned-fixture-entity",
        ["outcomePolicy"] = "submission-and-handling-are-not-proof-of-world-outcome",
    };

    private JsonObject ExecuteAction(JsonObject args)
    {
        if (_profile < DiagnosticProfile.Playtest)
            throw new DiagnosticException("access_denied", "This environment permits observation only.");
        var user = ResolveEntity(args, "actor");
        if (!_players.Sessions.Any(session => session.AttachedEntity == user) &&
            !(_profile == DiagnosticProfile.FixtureAdmin && _fixtures.Values.Any(fixture => fixture.Entities.Contains(user))))
            throw new DiagnosticException("access_denied", "The actor must be an attached player or an owned fixture entity.");
        var action = args.RequiredString("action", 64);
        bool? handled;
        switch (action)
        {
            case "interact":
                var target = ResolveEntity(args, "target");
                _interaction.UserInteraction(user, Transform(target).Coordinates, target,
                    altInteract: args["alternative"]?.GetValue<bool>() ?? false);
                handled = null;
                break;
            case "pickup":
                var item = ResolveEntity(args, "target");
                handled = _interaction.InRangeUnobstructed(user, item) && _hands.TryPickupAnyHand(user, item);
                break;
            case "drop":
                handled = _hands.TryDrop(user);
                break;
            case "use_in_hand":
                handled = _hands.TryGetActiveItem(user, out var held) && _interaction.UseInHandInteraction(user, held.Value);
                break;
            default:
                throw new DiagnosticException("unsupported_action", "This action is not in the authoritative action catalogue.");
        }
        return new JsonObject { ["action"] = action, ["submitted"] = true, ["handled"] = handled,
            ["actor"] = GetNetEntity(user).ToString(), ["executionPath"] = "server-authoritative",
            ["outcomeConfirmed"] = false };
    }

    private void RequireFixtureAdmin()
    {
        if (_profile != DiagnosticProfile.FixtureAdmin || !_isolated)
            throw new DiagnosticException("access_denied", "Fixture administration requires an explicitly isolated fixture-admin environment.");
    }

    private Fixture GetFixture(JsonObject args, out string id)
    {
        RequireFixtureAdmin();
        id = args.RequiredString("fixtureId", 64);
        if (!_fixtures.TryGetValue(id, out var fixture) || Deleted(fixture.Map))
            throw new DiagnosticException("fixture_not_found", "The fixture does not exist in this world.");
        return fixture;
    }

    private JsonObject CreateFixture(JsonObject args)
    {
        RequireFixtureAdmin();
        if (_fixtures.Count >= 8)
            throw new DiagnosticException("fixture_limit", "At most eight fixtures may exist at once.");
        var id = Guid.NewGuid().ToString("N");
        var map = _maps.CreateMap();
        var fixture = new Fixture(map);
        CreateFixtureFloor(fixture);
        _fixtures[id] = fixture;
        return new JsonObject { ["fixtureId"] = id, ["map"] = GetNetEntity(map).ToString(),
            ["grid"] = GetNetEntity(fixture.Grid!.Value).ToString(), ["isolated"] = true };
    }

    private JsonObject SpawnFixtureEntity(JsonObject args)
    {
        var fixture = GetFixture(args, out var id);
        if (fixture.Entities.Count >= 32)
            throw new DiagnosticException("fixture_limit", "At most 32 explicitly spawned entities may belong to one fixture.");
        var prototypeId = args.RequiredString("prototype");
        if (!_prototypes.TryIndex<EntityPrototype>(prototypeId, out var prototype))
            throw new DiagnosticException("prototype_not_found", "The entity prototype is not spawnable.");
        var position = new Vector2(args.BoundedFloat("x", 0, -16, 16), args.BoundedFloat("y", 0, -16, 16));
        var uid = Spawn(prototype.ID, new EntityCoordinates(fixture.Grid ?? fixture.Map, position));
        fixture.Entities.Add(uid);
        return new JsonObject { ["fixtureId"] = id, ["netEntity"] = GetNetEntity(uid).ToString(), ["prototype"] = prototype.ID };
    }

    private JsonObject DestroyFixture(JsonObject args)
    {
        var fixture = GetFixture(args, out var id);
        QueueDel(fixture.Map);
        _fixtures.Remove(id);
        return new JsonObject { ["fixtureId"] = id, ["deletionQueued"] = true };
    }

    private JsonObject ResetFixture(JsonObject args)
    {
        var fixture = GetFixture(args, out var id);
        QueueDel(fixture.Map);
        fixture.Map = _maps.CreateMap();
        CreateFixtureFloor(fixture);
        fixture.Entities.Clear();
        return new JsonObject { ["fixtureId"] = id, ["map"] = GetNetEntity(fixture.Map).ToString(), ["oldMapDeletionQueued"] = true };
    }

    private void CreateFixtureFloor(Fixture fixture)
    {
        _maps.SetAmbientLight(Transform(fixture.Map).MapID, Color.White);
        var mixture = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        mixture.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
        mixture.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
        _atmosphere.SetMapAtmosphere(fixture.Map, false, mixture);
        var grid = _maps.CreateGridEntity(fixture.Map);
        fixture.Grid = grid.Owner;
        var tile = new Tile(_tiles["FloorSteel"].TileId);
        for (var x = -8; x <= 8; x++)
        for (var y = -8; y <= 8; y++)
            _maps.SetTile(grid, new Vector2i(x, y), tile);
        // Station floors can receive automatic roofs, which block ambient map light.
        var lamp = Spawn(null, new EntityCoordinates(grid.Owner, Vector2.Zero));
        var light = _fixtureLights.EnsureLight(lamp);
        _fixtureLights.SetColor(lamp, Color.White, light);
        _fixtureLights.SetRadius(lamp, 20f, light);
        _fixtureLights.SetEnergy(lamp, 2f, light);
        _fixtureLights.SetCastShadows(lamp, false, light);
        _fixtureLights.SetEnabled(lamp, true, light);
    }

    private JsonObject AttachFixtureActor(JsonObject args)
    {
        var fixture = GetFixture(args, out _);
        var actor = ResolveEntity(args, "actor");
        if (!fixture.Entities.Contains(actor) || !Guid.TryParse(args.RequiredString("userId", 64), out var userId))
            throw new DiagnosticException("invalid_arguments", "Select a fixture-owned actor and a connected user identifier.");
        var session = _players.Sessions.FirstOrDefault(player => player.UserId == new NetUserId(userId))
            ?? throw new DiagnosticException("player_not_found", "The selected client is not connected to this server.");
        if (!_minds.TryGetMind(session.UserId, out var mind, out _))
            mind = _minds.CreateMind(session.UserId, Name(actor)).Owner;
        _minds.TransferTo(mind.Value, actor, ghostCheckOverride: true, createGhost: false);
        _ticker.PlayerJoinGame(session, silent: true);
        return new JsonObject { ["attached"] = session.AttachedEntity == actor, ["actor"] = GetNetEntity(actor).ToString(),
            ["userId"] = session.UserId.ToString() };
    }

    private JsonObject ConfigureFixtureAccess(JsonObject args)
    {
        var fixture = GetFixture(args, out _);
        var uid = ResolveEntity(args);
        if (!fixture.Entities.Contains(uid))
            throw new DiagnosticException("access_denied", "Access setup is restricted to fixture-owned entities.");
        var tags = args["tags"]?.AsArray() ?? throw new DiagnosticException("invalid_arguments", "Supply an explicit access tag array.");
        if (tags.Count > 16)
            throw new DiagnosticException("invalid_arguments", "At most sixteen access tags are allowed.");
        var identifiers = tags.Select(tag => new ProtoId<AccessLevelPrototype>(tag!.GetValue<string>())).ToList();
        if (identifiers.Any(id => !_prototypes.HasIndex(id)))
            throw new DiagnosticException("prototype_not_found", "An access level prototype does not exist.");
        if (args["kind"]?.GetValue<string>() == "reader")
        {
            if (!TryComp<AccessReaderComponent>(uid, out var reader))
                throw new DiagnosticException("component_not_found", "The entity has no access reader.");
            _accessReaders.TrySetAccesses((uid, reader), identifiers);
        }
        else if (!_access.TrySetTags(uid, identifiers))
            throw new DiagnosticException("component_not_found", "The entity has no access provider.");
        return new JsonObject { ["configured"] = true, ["netEntity"] = GetNetEntity(uid).ToString() };
    }

    private JsonObject ConfigureFixturePower(JsonObject args)
    {
        var fixture = GetFixture(args, out _);
        var uid = ResolveEntity(args);
        if (!fixture.Entities.Contains(uid))
            throw new DiagnosticException("access_denied", "Power setup is restricted to fixture-owned entities.");
        SharedApcPowerReceiverComponent? receiver = null;
        if (!_power.ResolveApc(uid, ref receiver))
            throw new DiagnosticException("component_not_found", "The fixture entity has no APC power receiver.");
        _power.SetNeedsPower(uid, args["needsPower"]?.GetValue<bool>() ?? true, receiver);
        _power.SetPowerDisabled(uid, args["disabled"]?.GetValue<bool>() ?? false, receiver);
        return new JsonObject { ["configured"] = true, ["netEntity"] = GetNetEntity(uid).ToString() };
    }
}

#endif
