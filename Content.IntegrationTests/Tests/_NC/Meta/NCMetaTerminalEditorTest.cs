// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Meta.Projects;
using Content.Shared._NC.Meta;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>A server is a headless host: it keeps the service panel but never offers the source editor.</summary>
[TestFixture]
[TestOf(typeof(NCMetaWorkspaceSystem))]
public sealed class NCMetaTerminalEditorTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaWorkspaceSystem _workspace = default!;

    private EntityUid Spawn(string prototype)
    {
        Server.System<SharedMapSystem>().CreateMap(out var map);
        return SEntMan.SpawnEntity(prototype, new MapCoordinates(Vector2.Zero, map));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AServerOffersNoSourceEditorButATerminalDoes()
    {
        var server = Spawn("NCMetaServer");
        var terminal = Spawn("NCMetaTerminal");
        Assert.That(SEntMan.GetComponent<NCMetaTerminalComponent>(server).AllowEditor, Is.False);
        Assert.That(_workspace.CanOpenDevelopmentWorkspace(server), Is.False);
        Assert.That(SEntMan.GetComponent<NCMetaTerminalComponent>(terminal).AllowEditor, Is.True);
        Assert.That(_workspace.CanOpenDevelopmentWorkspace(terminal), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheEditorFlagReachesEveryServerVariant()
    {
        var gateway = Spawn("NCMetaMarketplaceService");
        Assert.That(_workspace.CanOpenDevelopmentWorkspace(gateway), Is.False, "The CitiNet gateway is a server too.");
    }
}
