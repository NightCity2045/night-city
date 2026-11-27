// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Cargo.Pawn;
using Content.Server.Cargo.Systems;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Weapons;

[TestFixture]
public sealed class NCWeaponFabricatorTest : GameTest
{
    private static readonly ProtoId<LatheRecipePackPrototype> WeaponPack = "NCWeaponsStatic";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    /// <summary>
    /// A printed gun, loaded as it leaves the lathe, must never sell to the best pawn broker for more than the
    /// materials it consumed. Otherwise the fabricator is a money printer.
    /// </summary>
    [Test]
    public async Task PrintedWeaponsCannotBePawnedAtAProfit()
    {
        var prototypes = SProtoMan;
        var factory = Server.ResolveDependency<IComponentFactory>();
        var pricing = Server.System<PricingSystem>();
        var map = await Pair.CreateTestMap();

        var bestRate = prototypes.EnumeratePrototypes<EntityPrototype>()
            .Where(proto => !proto.Abstract)
            .Select(proto => proto.TryGetComponent<NCPawnShopComponent>(out var shop, factory) ? shop.Percentage : 0)
            .Max();
        Assert.That(bestRate, Is.GreaterThan(0), "No pawn broker prototype was found.");

        var pack = prototypes.Index(WeaponPack);
        Assert.That(pack.Recipes, Is.Not.Empty);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var id in pack.Recipes)
                {
                    var recipe = prototypes.Index(id);
                    Assert.That(recipe.Result, Is.Not.Null, $"{id} has no result.");
                    var cost = recipe.Materials.Sum(pair => pair.Value * prototypes.Index(pair.Key).Price);

                    var gun = Server.EntMan.SpawnAtPosition(recipe.Result!.Value, map.GridCoords);
                    var payout = pricing.GetPrice(gun) * bestRate / 100d;
                    Server.EntMan.DeleteEntity(gun);

                    Assert.That(cost, Is.GreaterThan(payout),
                        $"{id} costs {cost:F0} in materials but pawns for {payout:F0} at {bestRate}%.");
                }
            });
        });
    }
}
