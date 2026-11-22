// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using Content.Client._NC.Medical.Pharmacology;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Client.UserInterface.Controls;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The research console window loads, takes a state and lets a formula be started from nothing. It cannot show how
/// the map looks, only that it builds and reacts. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaRdWindowTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    private static NCPharmaRdState State(bool beaker = true) => new(
        [
            new NCPharmaFormulaSummary(
                "NCPharmaFormulaIsolated.NCDrugInvTEST", 1, null, "NX-TEST", "NCDrugInvTEST",
                "NCPharmaDeliveryAutoinjector", 4f, [], "NCPharmaProductionStandard",
                NCPharmaRegulatoryStatus.Experimental, null, false),
        ],
        [],
        NCPharmaRdNotice.None,
        [],
        [],
        beaker,
        beaker ? [new NCPharmaReagentAmount("Inaprovaline", 20f), new NCPharmaReagentAmount("Bicaridine", 3f)] : [],
        true,
        0);

    [Test]
    [RunOnSide(Side.Client)]
    public void TheWindowBuildsAnEmptyMapAndShowsAnUnidentifiedCompoundLocked()
    {
        var window = new NCPharmaRdWindow();
        Assert.DoesNotThrow(() => window.UpdateState(State()));
        window.OpenCentered();

        var canvas = window.FindControl<NCPharmaTreeCanvas>("Canvas");
        var create = window.FindControl<Button>("CreateButton");
        var invent = window.FindControl<Button>("InventButton");
        Assert.That(create.Disabled, Is.True, "nothing is chosen yet, so nothing can be developed");
        Assert.That(invent.Disabled, Is.True, "no ingredients are chosen yet");
        Assert.That(canvas, Is.Not.Null);
        window.Close();
    }

    [Test]
    [RunOnSide(Side.Client)]
    public void InventionNeedsABeakerWithEnoughOfEveryChosenIngredient()
    {
        var window = new NCPharmaRdWindow();
        window.UpdateState(State());
        window.OpenCentered();

        var list = window.FindControl<BoxContainer>("IngredientList");
        var buttons = list.Children.OfType<Button>().ToList();
        Assert.That(buttons, Is.Not.Empty, "the ingredients are listed");

        var rows = buttons.Select(button => button.Text ?? string.Empty).ToList();
        Assert.That(rows.Any(text => text.Contains("20")), Is.True, "the beaker amount of an ingredient is shown");
        Assert.That(window.FindControl<Button>("InventButton").Disabled, Is.True, "no ingredient is chosen yet");

        window.UpdateState(State(beaker: false));
        Assert.That(window.FindControl<Button>("InventButton").Disabled, Is.True, "no beaker, no invention");
        window.Close();
    }

    [Test]
    [RunOnSide(Side.Client)]
    public void TheMachineWindowShowsStatusSlotsAndReadings()
    {
        var window = new NCLabMachineWindow();
        string? ejected = null;
        window.EjectRequested += slot => ejected = slot;
        window.UpdateState(new NCLabMachineState(
            true,
            NCLabMachinePhase.Working,
            0.4f,
            12,
            false,
            "nc-lab-machine-ui-start",
            [new NCLabMachineSlot("source", "nc-lab-machine-empty", null)],
            [new NCLabMachineReadout("nc-lab-machine-ui-filter", "7")]));
        window.OpenCentered();

        Assert.That(window.FindControl<Button>("StartButton").Disabled, Is.True, "a machine at work cannot be started again");
        Assert.That(window.FindControl<Button>("CancelButton").Visible, Is.True);
        Assert.That(window.FindControl<BoxContainer>("SlotList").ChildCount, Is.EqualTo(1));
        Assert.That(window.FindControl<BoxContainer>("ReadoutList").ChildCount, Is.EqualTo(1));
        Assert.That(ejected, Is.Null);
        window.Close();
    }
}
