// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Housing;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._NC.Housing;

[TestFixture]
[TestOf(typeof(NCPropertySystem))]
[NonParallelizable]
public sealed class NCPropertyTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    private const string PropertyId = "NCTestFlat";

    /// <summary>
    /// A flat with a bathroom behind an inner door, its front door on the west wall:
    ///   y5  W W W W W W W
    ///   y4  W . . . W . W
    ///   y3  D . m . d . W     D front door, d bathroom door, m marker
    ///   y2  W . . . W . W
    ///   y1  W W W W W W W
    ///      x1 2 3 4 5 6 7
    /// with open floor all around.
    /// </summary>
    private (EntityUid FrontDoor, EntityUid BathroomDoor) BuildFlat(TestMapData map)
    {
        var maps = Server.System<SharedMapSystem>();
        for (var x = -1; x <= 9; x++)
        {
            for (var y = -1; y <= 7; y++)
                maps.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), new Tile(1));
        }

        EntityCoordinates At(int x, int y) => new(map.Grid.Owner, x + 0.5f, y + 0.5f);
        for (var x = 1; x <= 7; x++)
        {
            SEntMan.SpawnEntity("WallSolid", At(x, 1));
            SEntMan.SpawnEntity("WallSolid", At(x, 5));
        }

        foreach (var y in new[] { 2, 4 })
        {
            SEntMan.SpawnEntity("WallSolid", At(1, y));
            SEntMan.SpawnEntity("WallSolid", At(5, y));
            SEntMan.SpawnEntity("WallSolid", At(7, y));
        }

        SEntMan.SpawnEntity("WallSolid", At(7, 3));
        var front = SEntMan.SpawnEntity("Airlock", At(1, 3));
        var bathroom = SEntMan.SpawnEntity("Airlock", At(5, 3));
        var marker = SEntMan.SpawnEntity("NCPropertyMarker", At(3, 3));
        var component = SEntMan.GetComponent<NCPropertyMarkerComponent>(marker);
        component.Id = PropertyId;
        component.Name = "Test flat";
        component.RentPerHour = 10;
        return (front, bathroom);
    }

    /// <summary>The flat takes in its bathroom; only the front door locks, and only to a current card.</summary>
    [Test]
    public async Task FlatLocksToCurrentCards()
    {
        var properties = Server.System<NCPropertySystem>();
        var map = await Pair.CreateTestMap();
        EntityUid front = default;
        EntityUid bathroom = default;

        await Server.WaitPost(() => (front, bathroom) = BuildFlat(map));
        await Server.WaitRunTicks(1);

        await Server.WaitAssertion(() =>
        {
            properties.EnsureIndexed();
            Assert.That(properties.Properties.TryGetValue(PropertyId, out var flat), Is.True);
            Assert.That(flat!.Tiles.Count, Is.EqualTo(12), "Living room and bathroom.");
            Assert.That(flat.Doors.Select(door => door.Door), Is.EquivalentTo(new[] { front }), "Only the front door locks.");
            Assert.That(SEntMan.HasComponent<NCPropertyDoorComponent>(bathroom), Is.False);

            var outside = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, -0.5f, 3.5f));
            var inside = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 3.5f, 2.5f));
            var key = flat.Doors[0].Key;
            Assert.That(properties.CanOpen(outside, flat, key), Is.False, "A vacant flat stays locked.");
            Assert.That(properties.CanOpen(inside, flat, key), Is.True, "Anyone inside can leave.");

            var lease = properties.GetLease(PropertyId);
            lease.Tenant = 1;
            lease.KeyVersion = 3;
            lease.PaidUntil = new NCActiveWorldTime(TimeSpan.FromMinutes(30).Ticks);

            var card = SEntMan.SpawnEntity(NCPropertySystem.CardPrototype, SEntMan.GetComponent<TransformComponent>(outside).Coordinates);
            var cardComp = SEntMan.GetComponent<NCPropertyCardComponent>(card);
            cardComp.PropertyId = PropertyId;
            cardComp.DoorKey = key;
            cardComp.KeyVersion = 3;
            Assert.That(Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>().TryPickupAnyHand(outside, card), Is.True);
            Assert.That(properties.CanOpen(outside, flat, key), Is.True, "The tenant's card opens the door.");

            cardComp.KeyVersion = 2;
            Assert.That(properties.CanOpen(outside, flat, key), Is.False, "A card of an older lease does not.");
            cardComp.KeyVersion = 3;

            properties.CheckLeases(new NCActiveWorldTime(TimeSpan.FromMinutes(31).Ticks));
            Assert.That(lease.Tenant, Is.Null, "Unpaid time evicts the tenant.");
            Assert.That(lease.KeyVersion, Is.EqualTo(4), "Eviction changes the locks.");
            Assert.That(properties.CanOpen(outside, flat, key), Is.False, "The old card is dead.");
        });
    }
}
