// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The raw material players brew in a beaker: the data reactions must really turn precursors into broth when
/// warm and spoil it into sludge when overheated. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaBrewingTest : GameTest
{
    private static readonly ProtoId<ReagentPrototype> Substrate = "NCPharmaSubstrateNeuro";
    private static readonly ProtoId<ReagentPrototype> Solvent = "Ethanol";
    private static readonly ProtoId<ReagentPrototype> Broth = "NCPharmaBrothNeuro";
    private static readonly ProtoId<ReagentPrototype> OtherBroth = "NCPharmaBrothCardio";
    private static readonly ProtoId<ReagentPrototype> Sludge = "NCPharmaSludge";

    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private Entity<SolutionComponent> Beaker()
    {
        _maps.CreateMap(out var map);
        var beaker = SEntMan.SpawnEntity("Beaker", new MapCoordinates(Vector2.Zero, map));
        Assert.That(_solutions.TryGetSolution(beaker, "beaker", out var soln, out _), Is.True);
        return soln!.Value;
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WarmMixtureBrewsIntoBroth()
    {
        var soln = Beaker();
        _solutions.SetTemperature(soln, 345f);
        Assert.That(_solutions.TryAddReagent(soln, Substrate, 20), Is.True);
        Assert.That(_solutions.TryAddReagent(soln, Solvent, 10), Is.True);

        var solution = soln.Comp.Solution;
        Assert.Multiple(() =>
        {
            Assert.That((float) solution.GetTotalPrototypeQuantity(Broth), Is.EqualTo(30f).Within(0.5f),
                "two parts substrate and one part solvent must brew into broth");
            Assert.That((float) solution.GetTotalPrototypeQuantity(Sludge), Is.EqualTo(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ColdMixtureDoesNotBrew()
    {
        var soln = Beaker();
        _solutions.SetTemperature(soln, 293f);
        Assert.That(_solutions.TryAddReagent(soln, Substrate, 20), Is.True);
        Assert.That(_solutions.TryAddReagent(soln, Solvent, 10), Is.True);

        Assert.That((float) soln.Comp.Solution.GetTotalPrototypeQuantity(Broth), Is.EqualTo(0f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OverheatedBrothSpoilsIntoSludge()
    {
        var soln = Beaker();
        _solutions.SetTemperature(soln, 345f);
        Assert.That(_solutions.TryAddReagent(soln, Substrate, 20), Is.True);
        Assert.That(_solutions.TryAddReagent(soln, Solvent, 10), Is.True);

        _solutions.SetTemperature(soln, 430f);
        _solutions.UpdateChemicals(soln);

        var solution = soln.Comp.Solution;
        Assert.Multiple(() =>
        {
            Assert.That((float) solution.GetTotalPrototypeQuantity(Sludge), Is.GreaterThan(0f),
                "an overheated brew must leave sludge behind");
            Assert.That((float) solution.GetTotalPrototypeQuantity(Broth), Is.LessThan(30f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EachKindOfSubstrateBrewsItsOwnBroth()
    {
        var soln = Beaker();
        _solutions.SetTemperature(soln, 345f);
        Assert.That(_solutions.TryAddReagent(soln, "NCPharmaSubstrateCardio", 20), Is.True);
        Assert.That(_solutions.TryAddReagent(soln, Solvent, 10), Is.True);

        var solution = soln.Comp.Solution;
        Assert.Multiple(() =>
        {
            Assert.That((float) solution.GetTotalPrototypeQuantity(OtherBroth), Is.EqualTo(30f).Within(0.5f));
            Assert.That((float) solution.GetTotalPrototypeQuantity(Broth), Is.EqualTo(0f));
        });
    }
}

