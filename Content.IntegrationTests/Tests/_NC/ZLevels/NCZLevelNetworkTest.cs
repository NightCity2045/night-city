// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

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
}
