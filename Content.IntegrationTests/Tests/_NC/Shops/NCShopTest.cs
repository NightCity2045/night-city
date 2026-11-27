// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Bank;
using Content.Server._NC.Shops;
using Content.Shared._NC.Shops;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Shops;

[TestFixture]
[TestOf(typeof(NCShopSystem))]
[NonParallelizable]
public sealed class NCShopTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    /// <summary>Every catalog line is a real entity with a positive price and stock.</summary>
    [Test]
    public async Task CatalogsAreConsistent()
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var catalog in SProtoMan.EnumeratePrototypes<NCShopCatalogPrototype>())
            {
                Assert.That(catalog.Entries, Is.Not.Empty, catalog.ID);
                foreach (var entry in catalog.Entries)
                {
                    Assert.That(SProtoMan.HasIndex<EntityPrototype>(entry.Item), $"{catalog.ID}: {entry.Item}");
                    Assert.That(entry.Price, Is.GreaterThan(0), $"{catalog.ID}: {entry.Item}");
                    Assert.That(entry.Stock, Is.GreaterThan(0), $"{catalog.ID}: {entry.Item}");
                }
            }
        });
    }

    /// <summary>
    /// Small goods go into the buyer's hands, furniture to the pickup point; the account pays the price, stock
    /// runs out, and an empty account cannot buy.
    /// </summary>
    [Test]
    public async Task BuyingTakesMoneyAndStock()
    {
        var shops = Server.System<NCShopSystem>();
        var hands = Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var map = await Pair.CreateTestMap();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var groceryUid = SEntMan.SpawnEntity("NCShopGroceriesVendomat", coords);
            var furnitureUid = SEntMan.SpawnEntity("NCShopFurnitureVendomat", coords.Offset(new System.Numerics.Vector2(3, 0)));
            var pickup = SEntMan.SpawnEntity("NCShopPickupPoint", coords.Offset(new System.Numerics.Vector2(3, 2)));
            var buyer = SEntMan.SpawnEntity("MobHuman", coords.Offset(new System.Numerics.Vector2(1, 0)));
            var account = new NCBankAccountRecord(990010, "NC-990010", 4250, null, 1000);
            var poor = new NCBankAccountRecord(990011, "NC-990011", 4251, null, 0);

            var grocery = new Entity<NCShopComponent>(groceryUid, SEntMan.GetComponent<NCShopComponent>(groceryUid));
            var furniture = new Entity<NCShopComponent>(furnitureUid, SEntMan.GetComponent<NCShopComponent>(furnitureUid));
            var groceryCatalog = SProtoMan.Index(grocery.Comp.Catalog);
            var furnitureCatalog = SProtoMan.Index(furniture.Comp.Catalog);

            var bread = groceryCatalog.Entries.FindIndex(entry => entry.Item == "FoodBreadPlain");
            var startStock = shops.GetStock(grocery)[bread];
            Assert.That(shops.TryBuy(grocery, buyer, account, bread, out _), Is.True);
            Assert.That(account.Balance, Is.EqualTo(1000 - groceryCatalog.Entries[bread].Price));
            Assert.That(shops.GetStock(grocery)[bread], Is.EqualTo(startStock - 1));
            var held = hands.EnumerateHeld(buyer).Single();
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID, Is.EqualTo("FoodBreadPlain"));

            Assert.That(shops.TryBuy(grocery, buyer, poor, bread, out var funds), Is.False);
            Assert.That(funds, Is.EqualTo("nc-shop-error-funds"));

            var bed = furnitureCatalog.Entries.FindIndex(entry => entry.Item == "Bed");
            Assert.That(shops.TryBuy(furniture, buyer, account, bed, out _), Is.True);
            var pickupPosition = SEntMan.GetComponent<TransformComponent>(pickup).Coordinates.Position;
            var beds = SEntMan.EntityQuery<MetaDataComponent>()
                .Where(meta => meta.EntityPrototype?.ID == "Bed")
                .Select(meta => SEntMan.GetComponent<TransformComponent>(meta.Owner).Coordinates.Position)
                .ToList();
            Assert.That(beds, Does.Contain(pickupPosition), "The bed waits at the pickup point.");

            var stock = shops.GetStock(furniture);
            stock[bed] = 0;
            Assert.That(shops.TryBuy(furniture, buyer, account, bed, out var soldOut), Is.False);
            Assert.That(soldOut, Is.EqualTo("nc-shop-error-sold-out"));
        });
    }
}
