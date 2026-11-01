// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaNetworkSystem))]
public sealed class NCMetaPhysicalNetworkTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaNetworkSystem _network = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;

    // Unpowered-APC behavior is tested by the hardware fixtures. These minimal physical ports have
    // no APC requirement, so routing tests exercise cables/radio rather than an unrelated power grid.
    [TestPrototypes]
    private const string Ports = """
        - type: entity
          id: NCMetaIntegrationPhysicalPort
          components:
          - type: NCMetaHost
            profile: NCMetaNetworkHost
          - type: NCMetaDevice
          - type: NCMetaPort
            networkId: integration-local
          - type: NCMetaMessagePort
          - type: DeviceNetwork
            deviceNetId: Private
            receiveFrequency: 1945
            transmitFrequency: 1945
        """;

    private (MapId Map, EntityUid Actor) Scene()
    {
        Server.System<SharedMapSystem>().CreateMap(out var map);
        return (map, SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map)));
    }

    private EntityUid Port(MapId map, Vector2 position) =>
        SEntMan.SpawnEntity("NCMetaIntegrationPhysicalPort", new MapCoordinates(position, map));

    private EntityUid Cable(MapId map, EntityUid actor, EntityUid first, EntityUid second)
    {
        var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", new MapCoordinates(Vector2.Zero, map));
        Assert.That(_network.TryConnectCable(cable, actor, first), Is.True);
        Assert.That(_network.TryConnectCable(cable, actor, second), Is.True);
        return cable;
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ASharedSegmentAndNearbyPositionsDoNotReplaceTheTwoPhysicalCableEnds()
    {
        var (map, actor) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, Vector2.UnitX);
        var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", new MapCoordinates(Vector2.Zero, map));
        Assert.That(_network.CanRoute(first, second), Is.False);
        Assert.That(_network.TryConnectCable(cable, actor, first), Is.True);
        Assert.That(_network.CanRoute(first, second), Is.False);
        Assert.That(_network.TryConnectCable(cable, actor, second), Is.True);
        Assert.That(_network.CanRoute(first, second), Is.True);
        Assert.That(_network.CanRoute(second, first), Is.True);
        Assert.That(_devices.TryGetEntry(first, out var firstDevice), Is.True);
        Assert.That(_devices.TryGetEntry(second, out var secondDevice), Is.True);
        var ends = SComp<NCMetaNetworkCableComponent>(cable);
        Assert.That(ends.DeviceAId, Is.EqualTo(firstDevice.Id));
        Assert.That(ends.DeviceBId, Is.EqualTo(secondDevice.Id));
        Assert.That(_network.TryDisconnectCable(cable, actor), Is.True);
        Assert.That(_network.CanRoute(first, second), Is.False);
        Assert.That(ends.DeviceAId, Is.Empty);
        Assert.That(ends.DeviceBId, Is.Empty);
        Assert.That(ends.PortA, Is.Null);
        Assert.That(ends.PortB, Is.Null);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CableLengthAndCurrentMapAreCheckedAgainWhenTheRouteIsUsed()
    {
        var (map, actor) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, Vector2.UnitX);
        var cable = Cable(map, actor, first, second);
        var ends = SComp<NCMetaNetworkCableComponent>(cable);
        ends.MaximumLength = 2;
        var transform = Server.System<SharedTransformSystem>();
        Assert.That(_network.CanRoute(first, second), Is.True);
        transform.SetMapCoordinates(second, new MapCoordinates(new Vector2(3, 0), map));
        Assert.That(_network.CanRoute(first, second), Is.False);
        transform.SetMapCoordinates(second, new MapCoordinates(Vector2.UnitX, map));
        Assert.That(_network.CanRoute(first, second), Is.True);
        ends.MaximumLength = float.NaN;
        Assert.That(_network.CanRoute(first, second), Is.False);
        ends.MaximumLength = 2;
        Server.System<SharedMapSystem>().CreateMap(out var otherMap);
        transform.SetMapCoordinates(second, new MapCoordinates(Vector2.UnitX, otherMap));
        Assert.That(_network.CanRoute(first, second), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void GatewayPolicyIsBidirectionalButAnOfflineIntermediateCannotForward()
    {
        var (map, actor) = Scene();
        var local = Port(map, Vector2.Zero);
        var gateway = Port(map, Vector2.UnitX);
        var remote = Port(map, Vector2.UnitY);
        SComp<NCMetaPortComponent>(gateway).NetworkId = "integration-remote";
        SComp<NCMetaPortComponent>(remote).NetworkId = "integration-remote";
        Cable(map, actor, local, gateway);
        Cable(map, actor, gateway, remote);
        Assert.That(_network.CanRoute(local, remote), Is.False);
        Assert.That(_network.CanRoute(remote, local), Is.False);
        SComp<NCMetaMessagePortComponent>(gateway).GatewayNetworks.Add("integration-local");
        Assert.That(_network.CanRoute(local, remote), Is.True);
        Assert.That(_network.CanRoute(remote, local), Is.True);
        SComp<NCMetaHostComponent>(gateway).Disabled = true;
        Assert.That(_network.CanRoute(local, remote), Is.False);
        SComp<NCMetaHostComponent>(gateway).Disabled = false;
        Assert.That(_network.CanRoute(local, remote), Is.True);
        SComp<NCMetaMessagePortComponent>(gateway).GatewayNetworks.Clear();
        Assert.That(_network.CanRoute(local, remote), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WirelessRequiresBothPhysicalRadiosAndTheSmallerNativeRange()
    {
        var (map, _) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, new Vector2(7, 0));
        var left = SComp<NCMetaPortComponent>(first);
        var right = SComp<NCMetaPortComponent>(second);
        left.Wireless = true;
        Assert.That(_network.CanRoute(first, second), Is.False);
        right.Wireless = true;
        Assert.That(_network.CanRoute(first, second), Is.True);
        right.RadioProfile = "NCMetaShieldedRadio";
        Assert.That(_network.CanRoute(first, second), Is.False, "The shielded radio's native maximum is six tiles.");
        Server.System<SharedTransformSystem>().SetMapCoordinates(second, new MapCoordinates(new Vector2(5, 0), map));
        Assert.That(_network.CanRoute(first, second), Is.True);
        left.Range = float.NaN;
        Assert.That(_network.CanRoute(first, second), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ActualBatteryPoweredInterferenceRespectsTheProfileAndStopsWithItsPower()
    {
        var (map, _) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, Vector2.UnitX);
        var left = SComp<NCMetaPortComponent>(first);
        var right = SComp<NCMetaPortComponent>(second);
        left.Wireless = right.Wireless = true;
        Assert.That(_network.CanRoute(first, second), Is.True);
        var jammer = SEntMan.SpawnEntity("NCMetaRadioJammer", new MapCoordinates(Vector2.UnitY, map));
        Assert.That(_network.CanRoute(first, second), Is.False);
        left.RadioProfile = right.RadioProfile = "NCMetaShieldedRadio";
        Assert.That(_network.CanRoute(first, second), Is.True);
        SComp<NCMetaRadioInterferenceComponent>(jammer).Strength = 2;
        Assert.That(_network.CanRoute(first, second), Is.False);
        Assert.That(Server.System<PowerCellSystem>().TryGetBatteryFromSlot(jammer, out var battery), Is.True);
        Server.System<SharedBatterySystem>().ChangeCharge(battery!.Value.Owner, -100000f);
        Assert.That(_network.CanRoute(first, second), Is.True, "An empty physical cell cannot power native interference.");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RemovingAnEndpointKeepsItsStableCableIdentityWithoutConnectingAnUnrelatedReplacement()
    {
        var (map, actor) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, Vector2.UnitX);
        var cable = Cable(map, actor, first, second);
        var ends = SComp<NCMetaNetworkCableComponent>(cable);
        var stable = ends.DeviceAId;
        SEntMan.DeleteEntity(first);
        var unrelated = Port(map, Vector2.Zero);
        Assert.That(ends.DeviceAId, Is.EqualTo(stable));
        Assert.That(ends.PortA, Is.Null);
        Assert.That(_network.CanRoute(unrelated, second), Is.False);
        SEntMan.DeleteEntity(second);
        Assert.That(ends.PortB, Is.Null, "The surviving endpoint must remain discoverable after route adjacency is removed.");
        var loose = SEntMan.SpawnEntity("NCMetaNetworkCable", new MapCoordinates(Vector2.Zero, map));
        Assert.That(_network.TryConnectCable(loose, actor, unrelated), Is.True);
        var looseEnd = SComp<NCMetaNetworkCableComponent>(loose);
        var looseStableId = looseEnd.DeviceAId;
        SEntMan.DeleteEntity(unrelated);
        Assert.That(looseEnd.PortA, Is.Null, "A one-ended cable has no route adjacency but still owns a runtime endpoint cache.");
        Assert.That(looseEnd.DeviceAId, Is.EqualTo(looseStableId));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ARejectedSeventeenthCableDoesNotLeaveAnEdgeInThePhysicalGraph()
    {
        var (map, actor) = Scene();
        var first = Port(map, Vector2.Zero);
        var second = Port(map, Vector2.UnitX);
        var admitted = new List<EntityUid>();
        for (var i = 0; i < 16; i++) admitted.Add(Cable(map, actor, first, second));
        var rejected = SEntMan.SpawnEntity("NCMetaNetworkCable", new MapCoordinates(Vector2.Zero, map));
        Assert.That(_network.TryConnectCable(rejected, actor, first), Is.True);
        Assert.That(_network.TryConnectCable(rejected, actor, second), Is.False);
        Assert.That(SComp<NCMetaNetworkCableComponent>(rejected).PortB, Is.Null);
        Assert.That(SComp<NCMetaNetworkCableComponent>(rejected).DeviceBId, Is.Empty);
        foreach (var cable in admitted) SEntMan.DeleteEntity(cable);
        Assert.That(_network.CanRoute(first, second), Is.False);
    }
}
