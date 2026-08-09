// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.Dungeons.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.City.ZoneTemplates;

[TestFixture]
public sealed class NCZoneTemplateMarkerPrototypeTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task UniversalMapperMarkersExposeTheirSemanticComponents()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var components = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            AssertMarker<NCTemplateVerticalLinkMarkerComponent>(
                prototypes,
                components,
                "NCTemplateVerticalLinkMarker");
            AssertMarker<NCDungeonDynamicPointComponent>(prototypes, components, "NCDungeonDynamicPointMarker");
        });
    }

    private static void AssertMarker<TComponent>(
        IPrototypeManager prototypes,
        IComponentFactory components,
        string prototypeId)
        where TComponent : IComponent, new()
    {
        Assert.That(prototypes.TryIndex<EntityPrototype>(prototypeId, out var prototype), Is.True);
        Assert.That(prototype!.HasComp<TComponent>(components), Is.True);
    }
}
