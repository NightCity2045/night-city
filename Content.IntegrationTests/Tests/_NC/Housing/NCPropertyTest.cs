// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Bank;
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

    /// <summary>
    /// The tenant cuts a duplicate and changes the locks, offers the lease for resale, and a buyer pays the seller
    /// by bank transfer, takes over the paid time and gets fresh cards.
    /// </summary>
    [Test]
    public async Task KeysAndResale()
    {
        var properties = Server.System<NCPropertySystem>();
        var bank = Server.System<NCBankSystem>();
        var hands = Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var map = await Pair.CreateTestMap();
        await PoolManager.WaitUntil(Server, () => bank.Ready);
        await Server.WaitPost(() => BuildFlat(map));
        await Server.WaitRunTicks(1);

        var tenant = new NCBankAccountRecord(990001, "NC-990001", 4241, null, 5000);
        var buyer = new NCBankAccountRecord(990002, "NC-990002", 4242, null, 10000);
        bank.TryGetDepartmentAccount("NCPD", out var seller);
        var sellerStart = seller.Balance;
        EntityUid tenantMob = default;
        EntityUid buyerMob = default;
        NCPropertyLease lease = default!;

        await Server.WaitAssertion(() =>
        {
            properties.EnsureIndexed();
            lease = properties.GetLease(PropertyId);
            lease.Tenant = 4241;
            lease.KeyVersion = 1;
            lease.PaidUntil = new NCActiveWorldTime(TimeSpan.FromHours(2).Ticks);
            tenantMob = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, -0.5f, 3.5f));
            buyerMob = SEntMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, -0.5f, 2.5f));

            Assert.That(properties.TryDuplicate(tenantMob, tenant, PropertyId, 0, out _), Is.True);
            Assert.That(tenant.Balance, Is.EqualTo(5000 - properties.Config.CardPrice));
            var duplicate = hands.EnumerateHeld(tenantMob).Single();
            Assert.That(SEntMan.GetComponent<NCPropertyCardComponent>(duplicate).KeyVersion, Is.EqualTo(1));
            Assert.That(properties.TryDuplicate(buyerMob, buyer, PropertyId, 0, out var notYours), Is.False);
            Assert.That(notYours, Is.EqualTo("nc-property-error-not-yours"));

            Assert.That(properties.TryRekey(tenantMob, tenant, PropertyId, out _), Is.True);
            Assert.That(lease.KeyVersion, Is.EqualTo(2), "New locks.");
            Assert.That(tenant.Balance, Is.EqualTo(5000 - properties.Config.CardPrice - properties.Config.RekeyPrice));

            Assert.That(properties.TryListForSale(tenant, PropertyId, 0, null, out _), Is.False, "A price is needed.");
            Assert.That(properties.TryListForSale(tenant, PropertyId, 1000, "nc-990001", out _), Is.False, "Not to oneself.");
            Assert.That(properties.TryListForSale(tenant, PropertyId, 1000, "NC-999999", out _), Is.True);
            Assert.That(lease.ResaleSeller, Is.EqualTo("NC-990001"));

            // The test characters have no stored accounts: let a real account receive the money.
            lease.ResaleSeller = seller.Number;
        });

        (bool Success, string Error)? reserved = null;
        await Server.WaitPost(async () => reserved = await properties.TryBuyAsync(buyerMob, buyer, PropertyId));
        await PoolManager.WaitUntil(Server, () => reserved != null);
        Assert.That(reserved!.Value.Error, Is.EqualTo("nc-property-error-reserved"));

        (bool Success, string Error)? bought = null;
        await Server.WaitPost(async () =>
        {
            lease.ResaleBuyer = null;
            bought = await properties.TryBuyAsync(buyerMob, buyer, PropertyId);
        });
        await PoolManager.WaitUntil(Server, () => bought != null);

        await Server.WaitAssertion(() =>
        {
            Assert.That(bought!.Value.Success, Is.True, bought.Value.Error);
            Assert.That(lease.Tenant, Is.EqualTo(4242));
            Assert.That(lease.KeyVersion, Is.EqualTo(3), "The locks change for the new tenant.");
            Assert.That(lease.ResalePrice, Is.Null);
            Assert.That(lease.PaidUntil.Elapsed, Is.EqualTo(TimeSpan.FromHours(2)), "The paid time goes with the lease.");
            Assert.That(buyer.Balance, Is.EqualTo(10000 - 1000 - bank.GetFeeBasisPoints(buyer) * 1000 / 10000));
            Assert.That(seller.Balance, Is.EqualTo(sellerStart + 1000));
            var card = hands.EnumerateHeld(buyerMob).Single();
            Assert.That(SEntMan.GetComponent<NCPropertyCardComponent>(card).KeyVersion, Is.EqualTo(3));
        });
    }
}
