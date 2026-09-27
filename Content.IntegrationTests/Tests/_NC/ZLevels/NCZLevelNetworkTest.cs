// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.ZLevels.Core;
using Content.Server._NC.ZLevels.Lighting;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Light.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NC.ZLevels;

[TestFixture]
[TestOf(typeof(NCZLevelsSystem))]
public sealed class NCZLevelNetworkTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task ThreeLevelNetworkSupportsVerticalLookup()
    {
        var server = Pair.Server;
        var mapSystem = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();

        EntityUid mapBelow = default;
        EntityUid mapMain = default;
        EntityUid mapAbove = default;
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out var belowId);
            mapSystem.CreateMap(out var mainId);
            mapSystem.CreateMap(out var aboveId);

            mapBelow = mapSystem.GetMap(belowId);
            mapMain = mapSystem.GetMap(mainId);
            mapAbove = mapSystem.GetMap(aboveId);
            network = zLevels.CreateMapNetwork();

            // Add depth zero first, matching the station initialization path.
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [mapMain] = 0,
                [mapBelow] = -1,
                [mapAbove] = 1,
            }), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(zLevels.TryMapDown(mapMain, out var below), Is.True);
                Assert.That(below.Owner, Is.EqualTo(mapBelow));
                Assert.That(below.Comp.Depth, Is.EqualTo(-1));

                Assert.That(zLevels.TryMapUp(mapMain, out var above), Is.True);
                Assert.That(above.Owner, Is.EqualTo(mapAbove));
                Assert.That(above.Comp.Depth, Is.EqualTo(1));

                Assert.That(zLevels.TryMapOffset(mapBelow, 2, out var offset), Is.True);
                Assert.That(offset.Owner, Is.EqualTo(mapAbove));
                Assert.That(zLevels.TryGetMapNetwork(mapMain, out var resolved), Is.True);
                Assert.That(resolved.Owner, Is.EqualTo(network.Owner));
            });
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task CacheSupportsNegativeFirstSparseDepths()
    {
        var server = Pair.Server;
        var mapSystem = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();

        EntityUid bottom = default;
        EntityUid ground = default;
        EntityUid upper = default;
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out var bottomId);
            mapSystem.CreateMap(out var groundId);
            mapSystem.CreateMap(out var upperId);

            bottom = mapSystem.GetMap(bottomId);
            ground = mapSystem.GetMap(groundId);
            upper = mapSystem.GetMap(upperId);
            network = zLevels.CreateMapNetwork();

            // This is the order used by znetwork-load when a save contains basement floors.
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [bottom] = -2,
                [ground] = 0,
                [upper] = 3,
            }), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(network.Comp.SortedMin, Is.EqualTo(-2));
                Assert.That(network.Comp.SortedMax, Is.EqualTo(3));
                Assert.That(network.Comp.SortedZLevels, Has.Count.EqualTo(6));
                Assert.That(network.Comp.SortedZLevels[0], Is.EqualTo(bottom));
                Assert.That(network.Comp.SortedZLevels[1], Is.EqualTo(EntityUid.Invalid));
                Assert.That(network.Comp.SortedZLevels[2], Is.EqualTo(ground));
                Assert.That(network.Comp.SortedZLevels[3], Is.EqualTo(EntityUid.Invalid));
                Assert.That(network.Comp.SortedZLevels[4], Is.EqualTo(EntityUid.Invalid));
                Assert.That(network.Comp.SortedZLevels[5], Is.EqualTo(upper));
            });
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task RejectedBatchDoesNotPartiallyMutateNetwork()
    {
        var server = Pair.Server;
        var mapSystem = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();

        EntityUid first = default;
        EntityUid duplicateDepth = default;
        Entity<NCZMapNetworkComponent> network = default;

        var failureLevel = Pair.ServerLogHandler.FailureLevel;
        Pair.ServerLogHandler.FailureLevel = null;

        try
        {
            await server.WaitAssertion(() =>
            {
                mapSystem.CreateMap(out var firstId);
                mapSystem.CreateMap(out var duplicateId);
                first = mapSystem.GetMap(firstId);
                duplicateDepth = mapSystem.GetMap(duplicateId);
                network = zLevels.CreateMapNetwork();

                Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
                {
                    [first] = -1,
                    [duplicateDepth] = -1,
                }), Is.False);

                Assert.Multiple(() =>
                {
                    Assert.That(network.Comp.ZLevels, Is.Empty);
                    Assert.That(network.Comp.ZLevelByEntity, Is.Empty);
                    Assert.That(network.Comp.SortedZLevels, Is.Empty);
                });
            });
        }
        finally
        {
            Pair.ServerLogHandler.FailureLevel = failureLevel;
        }

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task FiveLevelNetworkCoversFullDownwardRenderBudget()
    {
        var server = Pair.Server;
        var mapSystem = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();

        var maps = new EntityUid[5];
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitAssertion(() =>
        {
            for (var depth = 0; depth < maps.Length; depth++)
            {
                mapSystem.CreateMap(out var mapId);
                maps[depth] = mapSystem.GetMap(mapId);
            }

            network = zLevels.CreateMapNetwork();
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps[0]] = 0,
                [maps[1]] = 1,
                [maps[2]] = 2,
                [maps[3]] = 3,
                [maps[4]] = 4,
            }), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(NCSharedZLevelsSystem.MaxZLevelsBelowRendering, Is.EqualTo(4));
                Assert.That(zLevels.TryMapOffset(maps[4], -4, out var bottom), Is.True);
                Assert.That(bottom.Owner, Is.EqualTo(maps[0]));
            });
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task DayNightCycleIsSynchronizedAboveGround()
    {
        var server = Pair.Server;
        var entities = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();

        EntityUid underground = default;
        EntityUid ground = default;
        EntityUid upper = default;
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitAssertion(() =>
        {
            mapSystem.CreateMap(out var undergroundId);
            mapSystem.CreateMap(out var groundId);
            mapSystem.CreateMap(out var upperId);
            underground = mapSystem.GetMap(undergroundId);
            ground = mapSystem.GetMap(groundId);
            upper = mapSystem.GetMap(upperId);
            network = zLevels.CreateMapNetwork();

            foreach (var map in new[] { underground, ground, upper })
                entities.EnsureComponent<MapLightComponent>(map).AmbientLightColor = Color.FromHex("#D8B059FF");

            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [underground] = -1,
                [ground] = 0,
                [upper] = 1,
            }), Is.True);

            // Shared map overrides are applied after NCZMapComponent has its final depth.
            foreach (var map in new[] { underground, ground, upper })
            {
                entities.AddComponent(map, new NCDayNightCycleComponent
                {
                    MinimumDepth = 0,
                    RoundStartHour = 8f,
                    Duration = TimeSpan.FromMinutes(60),
                    MinLightLevel = 0.3f,
                    MaxLightLevel = 3f,
                    ClipLight = 1.25f,
                    ClipLevel = Color.White,
                    MinLevel = Color.FromHex("#1A2680"),
                    MaxLevel = Color.White,
                });
            }

            Assert.Multiple(() =>
            {
                Assert.That(entities.HasComponent<LightCycleComponent>(underground), Is.False);
                Assert.That(entities.HasComponent<SunShadowCycleComponent>(underground), Is.False);
                Assert.That(entities.HasComponent<SunShadowComponent>(underground), Is.False);

                Assert.That(entities.TryGetComponent<LightCycleComponent>(ground, out var groundCycle), Is.True);
                Assert.That(entities.TryGetComponent<LightCycleComponent>(upper, out var upperCycle), Is.True);
                Assert.That(groundCycle!.Duration, Is.EqualTo(TimeSpan.FromMinutes(60)));
                Assert.That(groundCycle.Offset, Is.EqualTo(TimeSpan.FromMinutes(20)));
                Assert.That(upperCycle!.Duration, Is.EqualTo(groundCycle.Duration));
                Assert.That(upperCycle.Offset, Is.EqualTo(groundCycle.Offset));
                Assert.That(entities.HasComponent<SunShadowComponent>(ground), Is.True);
                Assert.That(entities.HasComponent<SunShadowCycleComponent>(upper), Is.True);
            });
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }
}
