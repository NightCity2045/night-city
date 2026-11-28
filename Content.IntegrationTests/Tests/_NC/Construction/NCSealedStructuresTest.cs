// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.IntegrationTests.Tests.Construction.Interaction;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Wires;

namespace Content.IntegrationTests.Tests._NC.Construction;

/// <summary>Walls and airlocks come down only with an RCD; airlock panels cannot be opened for hacking.</summary>
public sealed class NCSealedStructuresTest : InteractionTest
{
    [Test]
    public async Task AirlockPanelStaysSealed()
    {
        await SpawnTarget("Airlock");
        var panel = Comp<WiresPanelComponent>();
        Assert.That(panel.Open, Is.False);
        await InteractUsing(Screw);
        Assert.That(panel.Open, Is.False);
        await InteractUsing(Pry);
        AssertPrototype("Airlock");
    }

    [Test]
    public async Task ReinforcedWallIgnoresHandTools()
    {
        await StartDeconstruction("WallReinforced");
        await InteractUsing(Cut);
        await InteractUsing(Weld);
        AssertPrototype("WallReinforced");
    }
}
