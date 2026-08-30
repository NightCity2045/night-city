// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Stack;
using Content.Server._NC.Bank.Cash;
using Content.Shared._NC.Bank.Components;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Economy;

[TestFixture]
[TestOf(typeof(NCCashTerminalEscrowSystem))]
public sealed class NCCashTerminalEscrowSystemTest : GameTest
{
    private static readonly ProtoId<StackPrototype> Currency = "Credit";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task DepositIdentityAndWithdrawalValueStayPrivateUntilResolved()
    {
        var map = await Pair.CreateTestMap();
        EntityUid terminal = default;
        EntityUid depositedCash = default;

        await Server.WaitAssertion(() =>
        {
            var containers = Server.System<SharedContainerSystem>();
            var stacks = Server.System<StackSystem>();
            var escrow = Server.System<NCCashTerminalEscrowSystem>();

            terminal = Server.EntMan.SpawnAtPosition("NCATM", map.GridCoords);
            depositedCash = stacks.SpawnNextToOrDrop(175, Currency, terminal);
            Assert.That(containers.TryGetContainer(terminal, NCAtmComponent.CashSlotId, out var input), Is.True);
            Assert.That(containers.Insert(depositedCash, input!, force: true), Is.True);

            var captured = escrow.CaptureDeposit(terminal, NCAtmComponent.CashSlotId, Currency, 1_000);
            Assert.That(captured, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(captured!.Amount, Is.EqualTo(175));
                Assert.That(captured.Entities, Is.EqualTo(new[] { depositedCash }));
                Assert.That(input.ContainedEntities, Is.Empty,
                    "The public input slot must not retain cash while its ledger outcome is unresolved.");
            });

            Assert.That(escrow.RestoreDeposit(terminal, NCAtmComponent.CashSlotId, captured), Is.True);
            Assert.That(input.ContainedEntities, Is.EqualTo(new[] { depositedCash }),
                "A rejected deposit must restore the exact original entity, not mint a replacement stack.");

            captured = escrow.CaptureDeposit(terminal, NCAtmComponent.CashSlotId, Currency, 1_000);
            Assert.That(captured, Is.Not.Null);
            escrow.Consume(captured!);

            var staged = escrow.StageWithdrawal(terminal, Currency, 250);
            Assert.That(staged, Is.Not.Null);
            Assert.That(staged!.Entities, Is.Not.Empty);
            Assert.That(staged.Entities.All(entity => Server.EntMan.GetComponent<TransformComponent>(entity).ParentUid ==
                                                     terminal), Is.True,
                "Every withdrawal stack must remain inside the terminal before durable completion.");

            Assert.That(escrow.ReleaseWithdrawal(terminal, staged), Is.True);
            Assert.That(staged.Entities.All(entity => Server.EntMan.GetComponent<TransformComponent>(entity).ParentUid !=
                                                     terminal), Is.True,
                "Only a resolved withdrawal may enter the playable map.");
        });

        await Server.WaitRunTicks(1);
        await Server.WaitAssertion(() => Assert.That(Server.EntMan.Deleted(depositedCash), Is.True));
    }
}
