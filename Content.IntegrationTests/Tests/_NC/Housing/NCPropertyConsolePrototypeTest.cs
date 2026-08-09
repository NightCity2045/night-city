// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Server.Construction.Components;
using Content.Shared._NC.Housing.Components;
using Content.Shared.Construction.Components;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Housing;

[TestFixture]
public sealed class NCPropertyConsolePrototypeTest : GameTest
{
    private static readonly EntProtoId ConsolePrototype = "NCPropertyConsole";
    private static readonly EntProtoId BoardPrototype = "NCPropertyConsoleCircuitboard";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task RealEstateConsoleAndBoardExposeRequiredComponents()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.That(prototypes.TryIndex(ConsolePrototype, out var console), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(console!.HasComp<NCPropertyConsoleComponent>(components), Is.True);
                Assert.That(console.HasComp<ActivatableUIComponent>(components), Is.True);
                Assert.That(console.HasComp<UserInterfaceComponent>(components), Is.True);
                Assert.That(console.HasComp<ComputerComponent>(components), Is.True);
            });

            Assert.That(prototypes.TryIndex(BoardPrototype, out var board), Is.True);
            Assert.That(board!.HasComp<ComputerBoardComponent>(components), Is.True);
        });
    }
}
