// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.ZLevels.Core.Components;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.ZLevels.Mapping.Prototypes;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Coordinates;

[TestFixture]
[TestOf(typeof(NCZNetworkId))]
public sealed class NCZNetworkIdPrototypeTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task PersistentZNetworkIdsAreValidAndUnique()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var componentFactory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var ownersById = new Dictionary<NCZNetworkId, string>();

            foreach (var zMap in prototypes.EnumeratePrototypes<NCZLevelMapPrototype>())
                AssertUnique(ownersById, zMap.NetworkId, $"zMap:{zMap.ID}");

            foreach (var gameMap in prototypes.EnumeratePrototypes<GameMapPrototype>())
            {
                foreach (var (stationId, station) in gameMap.Stations)
                {
                    if (!station.StationComponentOverrides.TryGetComponent<NCStationZLevelsComponent>(
                            componentFactory,
                            out var zLevels))
                    {
                        continue;
                    }

                    AssertUnique(
                        ownersById,
                        zLevels.NetworkId,
                        $"gameMap:{gameMap.ID}/station:{stationId}");
                }
            }
        });
    }

    private static void AssertUnique(
        Dictionary<NCZNetworkId, string> ownersById,
        NCZNetworkId networkId,
        string owner)
    {
        Assert.That(networkId.IsValid, Is.True, $"{owner} has an empty NCZNetworkId.");

        if (ownersById.TryAdd(networkId, owner))
            return;

        Assert.Fail(
            $"{owner} duplicates NCZNetworkId {networkId}, already assigned to {ownersById[networkId]}.");
    }
}
