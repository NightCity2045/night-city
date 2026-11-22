// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>
/// Laid data cable: pieces on neighbouring tiles form a bus, a port attaches to the pieces on its own tile or beside it,
/// and cutting a piece breaks the link. The ports are the unpowered test ports of the physical network fixture.
/// </summary>
[TestFixture]
[TestOf(typeof(NCMetaDataCableSystem))]
public sealed class NCMetaDataCableTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaNetworkSystem _network = default!;
    [SidedDependency(Side.Server)] private NCMetaDataCableSystem _cables = default!;

    private Entity<MapGridComponent> Row()
    {
        var maps = Server.System<SharedMapSystem>();
        maps.CreateMap(out var map);
        var grid = maps.CreateGridEntity(map);
        var plating = Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;
        for (var x = -1; x < 40; x++)
            maps.SetTile(grid, new Vector2i(x, 0), new Tile(plating));
        return grid;
    }

    private EntityUid Spawn(Entity<MapGridComponent> grid, string prototype, int x) =>
        SEntMan.SpawnEntity(prototype, new EntityCoordinates(grid.Owner, new Vector2(x + 0.5f, 0.5f)));

    private EntityUid Piece(Entity<MapGridComponent> grid, int x)
    {
        var piece = Spawn(grid, "NCMetaDataCable", x);
        Assert.That(SEntMan.GetComponent<TransformComponent>(piece).Anchored, Is.True, "The cable piece was not anchored to its tile.");
        return piece;
    }

    private EntityUid Port(Entity<MapGridComponent> grid, int x) => Spawn(grid, "NCMetaIntegrationPhysicalPort", x);

    [Test]
    [RunOnSide(Side.Server)]
    public void LaidPiecesLinkPortsBesideTheirEndsAndCuttingAPieceBreaksTheLink()
    {
        var grid = Row();
        var first = Port(grid, 0);
        var second = Port(grid, 5);
        Assert.That(_network.CanRoute(first, second), Is.False, "Ports without any cable are not linked.");

        var pieces = new List<EntityUid>();
        for (var x = 1; x <= 4; x++)
            pieces.Add(Piece(grid, x));
        Assert.That(_network.CanRoute(first, second), Is.True);
        Assert.That(_network.CanRoute(second, first), Is.True);

        SEntMan.DeleteEntity(pieces[1]);
        Assert.That(_network.CanRoute(first, second), Is.False, "Cutting a piece must break the bus.");

        Piece(grid, 2);
        Assert.That(_network.CanRoute(first, second), Is.True, "Laying the piece again must restore the bus.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheCableLengthIsTheNumberOfLaidTilesOnTheShortestPath()
    {
        var grid = Row();
        var first = Port(grid, 0);
        var second = Port(grid, 5);
        for (var x = 1; x <= 4; x++)
            Piece(grid, x);
        Assert.That(_cables.TryGetDistance(first, second, out var tiles), Is.True);
        Assert.That(tiles, Is.EqualTo(4));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ADeviceBesideTwoSeparateBusesDoesNotBridgeThem()
    {
        var grid = Row();
        var left = Port(grid, 0);
        var middle = Port(grid, 3);
        var right = Port(grid, 6);
        Piece(grid, 1);
        Piece(grid, 2);
        Piece(grid, 4);
        Piece(grid, 5);
        Assert.That(_network.CanRoute(left, middle), Is.True);
        Assert.That(_network.CanRoute(middle, right), Is.True);
        Assert.That(_network.CanRoute(left, right), Is.False, "A port attached to two buses is not a relay between them.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ALinkReservesMoreCapacityTheLongerTheLaidCable()
    {
        var grid = Row();
        var near = Port(grid, 5);
        var far = Port(grid, 21);
        var origin = Port(grid, 0);
        for (var x = 1; x <= 20; x++)
            Piece(grid, x);

        Assert.That(_network.TryGetLinkCost(origin, origin, out var self), Is.True);
        Assert.That(self, Is.Zero, "A host does not pay for its own ports.");
        Assert.That(_network.TryGetLinkCost(origin, near, out var short_), Is.True);
        Assert.That(_network.TryGetLinkCost(origin, far, out var long_), Is.True);
        Assert.That(short_, Is.EqualTo(NCMetaNetworkSystem.CableBaseCost + 1), "Four laid tiles round up to one distance unit.");
        Assert.That(long_, Is.GreaterThan(short_), "A longer cable must reserve more capacity.");
        Assert.That(long_, Is.EqualTo(NCMetaNetworkSystem.CableBaseCost + 3), "Twenty laid tiles are three distance units.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AnUnanchoredPieceIsNotPartOfTheBus()
    {
        var grid = Row();
        var first = Port(grid, 0);
        var second = Port(grid, 3);
        Piece(grid, 1);
        var loose = Piece(grid, 2);
        Assert.That(_network.CanRoute(first, second), Is.True);
        Server.System<SharedTransformSystem>().Unanchor(loose);
        Assert.That(_network.CanRoute(first, second), Is.False);
    }
}
