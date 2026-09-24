// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Atmos;
using Content.Server._NC.Atmos.Components;
using Content.Server._NC.ZLevels.Core.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Atmos;

[TestFixture]
[TestOf(typeof(NCUniformAirSystem))]
public sealed class NCUniformAirTest : GameTest
{
    private const string ALifeMap = "NCALifeTest";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task ALifeNetworkDefinesOnlyUniformBreathableAir()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.That(prototypes.TryIndex<GameMapPrototype>(ALifeMap, out var gameMap), Is.True);
            Assert.That(gameMap!.Stations.TryGetValue("Empty", out var station), Is.True);
            Assert.That(station!.StationComponentOverrides.TryGetComponent<NCStationZLevelsComponent>(
                components,
                out var zLevels), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(1 + zLevels!.MapsAbove.Count + zLevels.MapsBelow.Count, Is.EqualTo(9));
                Assert.That(zLevels.ZLevelsComponentOverrides.TryGetComponent<NCUniformAirComponent>(
                    components,
                    out _), Is.True);
                Assert.That(zLevels.ZLevelsComponentOverrides.TryGetComponent<MapAtmosphereComponent>(
                    components,
                    out var mapAtmosphere), Is.True);

                var mixture = mapAtmosphere!.Mixture;
                Assert.That(mapAtmosphere!.Space, Is.False);
                Assert.That(mixture.Immutable, Is.True);
                Assert.That(mixture.Temperature, Is.EqualTo(293.15f).Within(0.01f));
                Assert.That(mixture.Pressure,
                    Is.EqualTo(Atmospherics.OneAtmosphere).Within(1f));
                Assert.That(mixture.GetMoles(Gas.Oxygen), Is.GreaterThan(0f));
                Assert.That(mixture.GetMoles(Gas.Nitrogen), Is.GreaterThan(0f));

                for (var gas = 0; gas < Atmospherics.TotalNumberOfGases; gas++)
                {
                    if (gas is (int) Gas.Oxygen or (int) Gas.Nitrogen)
                        continue;

                    Assert.That(mixture.GetMoles(gas), Is.Zero,
                        $"Unexpected ambient gas {(Gas) gas} in NCALifeTest.");
                }
            });
        });
    }

    [Test]
    public async Task UniformAirMapRejectsAutomaticAndExplicitGridAtmosphere()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();

        MapId mapId = default;
        EntityUid gridUid = default;
        var concrete = new Tile(tiles["FloorConcrete"].TileId);

        await server.WaitAssertion(() =>
        {
            var mapUid = maps.CreateMap(out mapId);
            entities.EnsureComponent<NCUniformAirComponent>(mapUid);

            var grid = maps.CreateGridEntity(mapId);
            gridUid = grid.Owner;
            var floor = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 3; x++)
                    floor.Add((new Vector2i(x, y), concrete));
            }

            // Nine tiles exceed AutomaticAtmosSystem's seven-tile threshold.
            maps.SetTiles(gridUid, grid.Comp, floor);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<GridAtmosphereComponent>(gridUid), Is.False);
            Assert.That(entities.HasComponent<GasTileOverlayComponent>(gridUid), Is.False);

            var atmosphere = entities.EnsureComponent<GridAtmosphereComponent>(gridUid);
            Assert.That(atmosphere.Simulated, Is.False);
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.HasComponent<GridAtmosphereComponent>(gridUid), Is.False);
            Assert.That(entities.HasComponent<GasTileOverlayComponent>(gridUid), Is.False);
            maps.DeleteMap(mapId);
        });
    }
}
