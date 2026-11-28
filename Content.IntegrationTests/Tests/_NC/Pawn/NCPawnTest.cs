// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Pawn;
using Content.Shared._NC.Pawn;
using Content.Shared._NC.Shops;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Pawn;

[TestFixture]
public sealed class NCPawnTest : GameTest
{
    [SidedDependency(Side.Server)] private NCPawnSystem _pawn = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    /// <summary>Buying in a city shop and selling to even the best-paying buyer never turns a profit.</summary>
    [Test, RunOnSide(Side.Server)]
    public void NoShopGoodSellsForMoreThanItCosts()
    {
        var coords = Coords();
        var fence = Shop("NCPawnShopFence", coords);
        var problems = new List<string>();
        foreach (var catalog in _prototypes.EnumeratePrototypes<NCShopCatalogPrototype>())
        {
            foreach (var entry in catalog.Entries)
            {
                var item = SEntMan.SpawnEntity(entry.Item, coords);
                var payout = _pawn.GetPayout(fence, item);
                if (payout >= entry.Price)
                    problems.Add($"{catalog.ID}/{entry.Item}: retail {entry.Price}, fence pays {payout}");
                SEntMan.DeleteEntity(item);
            }
        }

        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }

    [Test, RunOnSide(Side.Server)]
    public void PrintedGoodsAreWorthTheirMaterials()
    {
        var coords = Coords();
        var fence = Shop("NCPawnShopFence", coords);
        var found = SEntMan.SpawnEntity("NCWeaponPistolStreetmaster", coords);
        var printed = SEntMan.SpawnEntity("NCWeaponPistolStreetmaster", coords);
        SEntMan.EnsureComponent<NCPrintedItemComponent>(printed).Recipe = "NCWeaponPistolStreetmaster";

        var prices = _prototypes.Index<NCPawnPricesPrototype>(NCPawnPricesPrototype.DefaultId);
        // 10 sheets of steel and 5 of plastic, plus the markup for the work
        var materials = (10 * prices.Materials["Steel"] + 5 * prices.Materials["Plastic"]) * prices.PrintedMarkup;
        Assert.That(_pawn.GetStreetValue(printed, prices), Is.EqualTo((int) System.MathF.Round(materials)));
        Assert.That(_pawn.GetPayout(fence, printed), Is.LessThan(_pawn.GetPayout(fence, found)));
    }

    [Test, RunOnSide(Side.Server)]
    public void CashAndIdentityAreRefused()
    {
        var coords = Coords();
        var fence = Shop("NCPawnShopFence", coords);
        Assert.That(_pawn.GetPayout(fence, SEntMan.SpawnEntity("PassengerIDCard", coords)), Is.Zero);
        var cash = SEntMan.SpawnEntity("SpaceCash10", coords);
        Assert.That(SEntMan.HasComponent<StackComponent>(cash), Is.True);
        Assert.That(_pawn.GetPayout(fence, cash), Is.Zero);
    }

    [Test, RunOnSide(Side.Server)]
    public void SaleDestroysTheGoodsPaysCashAndIsLogged()
    {
        var coords = Coords();
        var shop = Shop("NCPawnShopGoldenEddie", coords);
        var seller = SEntMan.SpawnEntity("MobHuman", coords);
        var item = SEntMan.SpawnEntity("NCMissionDrugBrick", coords);
        Assert.That(_hands.TryPickupAnyHand(seller, item), Is.True);

        var payout = _pawn.GetPayout(shop, item);
        Assert.That(payout, Is.EqualTo((int) (900 * 0.35f)));
        var before = _pawn.Log.Count;
        Assert.That(_pawn.TrySell(shop, seller, item), Is.True);
        Assert.That(SEntMan.Deleted(item) || SEntMan.IsQueuedForDeletion(item), Is.True);
        Assert.That(_pawn.Log.Count, Is.EqualTo(before + 1));
        var entry = _pawn.Log[^1];
        Assert.That(entry.Payout, Is.EqualTo(payout));
        Assert.That(entry.Seller, Is.Not.Empty);

        var cash = SEntMan.EntityQuery<StackComponent>()
            .Where(stack => stack.StackTypeId == "Credit")
            .Sum(stack => stack.Count);
        Assert.That(cash, Is.GreaterThanOrEqualTo(payout));
    }

    private EntityCoordinates Coords()
    {
        _maps.CreateMap(out var map);
        return new EntityCoordinates(_maps.GetMap(map), Vector2.Zero);
    }

    private Entity<NCPawnShopComponent> Shop(string id, EntityCoordinates coords)
    {
        var uid = SEntMan.SpawnEntity(id, coords);
        return (uid, SComp<NCPawnShopComponent>(uid));
    }
}
