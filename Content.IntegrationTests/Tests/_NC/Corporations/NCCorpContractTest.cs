// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Bank;
using Content.Server._NC.Corporations;
using Content.Shared._NC.Corporations;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Corporations;

[TestFixture]
[TestOf(typeof(NCCorpContractSystem))]
[NonParallelizable]
public sealed class NCCorpContractTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    [Test]
    public async Task EveryContractCanBeDescribed()
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var contract in SProtoMan.EnumeratePrototypes<NCCorpContractPrototype>())
            {
                Assert.That(contract.Reward, Is.GreaterThan(0), contract.ID);
                Assert.That(contract.Requirements, Is.Not.Empty, contract.ID);
                Assert.That(contract.Requirements.All(r => r.Amount > 0), contract.ID);
                Assert.That(SProtoMan.HasIndex(contract.Corporation), contract.ID);
            }
        });
    }

    /// <summary>
    /// A Militech employee fulfils an item contract from goods in a crate on the pallet: the goods are consumed,
    /// the reward is split between the field office and the corporation, and a new contract takes the slot.
    /// </summary>
    [Test]
    public async Task FulfillingAContractConsumesTheGoodsAndSplitsTheReward()
    {
        var contracts = Server.System<NCCorpContractSystem>();
        var bank = Server.System<NCBankSystem>();
        var minds = Server.System<SharedMindSystem>();
        var roles = Server.System<SharedRoleSystem>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);
        var map = await Pair.CreateTestMap();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var console = SEntMan.SpawnEntity("NCCorpSalesConsoleMilitech", coords);
            var pallet = SEntMan.SpawnEntity("NCCorpSalesPallet", coords.Offset(new System.Numerics.Vector2(1, 0)));
            var consoleEnt = new Entity<NCCorpSalesConsoleComponent>(console, SEntMan.GetComponent<NCCorpSalesConsoleComponent>(console));

            var open = contracts.GetOpenContracts("Militech");
            Assert.That(open, Has.Count.EqualTo(4));
            const int slot = 0;
            Assert.That(contracts.TrySetOpenContract("Militech", slot, "NCContractMilitechSaratoga"), Is.True);
            Assert.That(contracts.TrySetOpenContract("Militech", slot, "NCContractBiotechnicaBread"), Is.False,
                "A contract of another corporation cannot be opened.");
            var contract = SProtoMan.Index<NCCorpContractPrototype>("NCContractMilitechSaratoga");

            // An outsider cannot fulfil it.
            var outsider = SEntMan.SpawnEntity(null, coords);
            Assert.That(contracts.TryFulfill(consoleEnt, slot, outsider), Is.False);

            var worker = SEntMan.SpawnEntity(null, coords);
            SEntMan.EnsureComponent<MindContainerComponent>(worker);
            var mind = minds.CreateMind(null).Owner;
            minds.TransferTo(mind, worker);
            roles.MindAddJobRole(mind, jobPrototype: "MilitechOperative");

            // Not enough goods yet.
            Assert.That(contracts.TryFulfill(consoleEnt, slot, worker), Is.False);

            var crate = SEntMan.SpawnEntity("CrateGenericSteel", SEntMan.GetComponent<TransformComponent>(pallet).Coordinates);
            var storage = Server.System<Content.Shared.Storage.EntitySystems.SharedEntityStorageSystem>();
            var goods = new List<EntityUid>();
            foreach (var requirement in contract.Requirements.Cast<NCCorpItemRequirement>())
            {
                for (var i = 0; i < requirement.Amount; i++)
                {
                    var item = SEntMan.SpawnEntity(requirement.Item, SEntMan.GetComponent<TransformComponent>(pallet).Coordinates);
                    if (SEntMan.TryGetComponent<Content.Shared.Stacks.StackComponent>(item, out var stack) && stack.Count > 1)
                        Server.System<Content.Shared.Stacks.SharedStackSystem>().SetCount((item, stack), 1);
                    if (i % 2 == 0)
                        storage.Insert(item, crate);
                    goods.Add(item);
                }
            }

            bank.TryGetDepartmentAccount("Militech", out var department);
            bank.TryGetDepartmentAccount("MilitechHQ", out var corporation);
            var departmentBefore = department.Balance;
            var corporationBefore = corporation.Balance;

            Assert.That(contracts.TryFulfill(consoleEnt, slot, worker), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(department.Balance - departmentBefore, Is.EqualTo(contract.Reward / 2));
                Assert.That(corporation.Balance - corporationBefore, Is.EqualTo(contract.Reward - contract.Reward / 2));
                Assert.That(goods.All(item => SEntMan.Deleted(item) || SEntMan.IsQueuedForDeletion(item)), "The goods are consumed.");
                Assert.That(contracts.GetOpenContracts("Militech")[slot], Is.Not.EqualTo(contract.ID), "A new contract takes the slot.");
            });
        });
    }

    /// <summary>Factory implants count as finished; empty frames waiting for parts do not.</summary>
    [Test]
    public async Task OnlyFinishedImplantsCount()
    {
        var contracts = Server.System<NCCorpContractSystem>();
        var bank = Server.System<NCBankSystem>();
        var minds = Server.System<SharedMindSystem>();
        var roles = Server.System<SharedRoleSystem>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);
        var map = await Pair.CreateTestMap();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var console = SEntMan.SpawnEntity("NCCorpSalesConsoleMilitech", coords);
            var pallet = SEntMan.SpawnEntity("NCCorpSalesPallet", coords.Offset(new System.Numerics.Vector2(1, 0)));
            var palletCoords = SEntMan.GetComponent<TransformComponent>(pallet).Coordinates;
            var consoleEnt = new Entity<NCCorpSalesConsoleComponent>(console, SEntMan.GetComponent<NCCorpSalesConsoleComponent>(console));
            Assert.That(contracts.TrySetOpenContract("Militech", 1, "NCContractMilitechSandevistan"), Is.True);

            var worker = SEntMan.SpawnEntity(null, coords);
            SEntMan.EnsureComponent<MindContainerComponent>(worker);
            var mind = minds.CreateMind(null).Owner;
            minds.TransferTo(mind, worker);
            roles.MindAddJobRole(mind, jobPrototype: "MilitechTech");

            SEntMan.SpawnEntity("NCSandevistanImplantEmpty", palletCoords);
            SEntMan.SpawnEntity("NCSandevistanImplantEmpty", palletCoords);
            Assert.That(contracts.TryFulfill(consoleEnt, 1, worker), Is.False, "Empty frames are not finished implants.");

            SEntMan.SpawnEntity("NCSandevistanImplant", palletCoords);
            SEntMan.SpawnEntity("NCSandevistanImplant", palletCoords);
            Assert.That(contracts.TryFulfill(consoleEnt, 1, worker), Is.True, "Factory implants are finished.");
        });
    }
}
