// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Server.Construction.Components;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Missions.Components;
using Content.Shared.Construction.Components;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Missions;

[TestFixture]
public sealed class NCMissionConsolePrototypeTest : GameTest
{
    private static readonly EntProtoId ConsolePrototype = "NCMissionConsole";
    private static readonly EntProtoId BoardPrototype = "NCMissionConsoleCircuitboard";
    private static readonly NCMissionSlotId MissionSlotId =
        new(new NCZoneId(Guid.Parse("8d7b2c44-9a31-4f67-b8e2-6c5d4a3f1808")));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task PublicMissionConsoleBindsDefinitionAndStableSlot()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.That(prototypes.TryIndex(ConsolePrototype, out var console), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(console!.HasComp<NCMissionConsoleComponent>(components), Is.True);
                Assert.That(console.HasComp<ActivatableUIComponent>(components), Is.True);
                Assert.That(console.HasComp<UserInterfaceComponent>(components), Is.True);
                Assert.That(console.HasComp<ComputerComponent>(components), Is.True);
            });

            Assert.That(console!.TryGetComponent<NCMissionConsoleComponent>(out var binding, components), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(binding, Is.Not.Null);
                Assert.That(binding!.Definition.Id, Is.EqualTo("NCMission8x8"));
                Assert.That(binding.SlotId, Is.EqualTo(MissionSlotId));
            });

            Assert.That(prototypes.TryIndex(BoardPrototype, out var board), Is.True);
            Assert.That(board!.HasComp<ComputerBoardComponent>(components), Is.True);
        });
    }
}
