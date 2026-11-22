// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Lab;
using Content.Server.Power.Components;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Every laboratory machine is operated through its window: the start message runs the procedure a touch used to,
/// cancel stops it, and slots are emptied from the window. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCLabMachineUiTest : GameTest
{
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private static readonly string[] Machines =
    [
        "NCLabCultureStation", "NCLabMetabolicAnalyzer", "NCLabTissueIncubator", "NCPharmaPurifier",
        "NCPharmaExtractor", "NCPharmaSterileCabinet", "NCPharmaFormulationStation", "NCPharmaFillingStation",
        "NCBloodCentrifuge", "NCPharmaWasteBin",
    ];

    private (EntityUid User, MapCoordinates At) World()
    {
        _maps.CreateMap(out var map);
        var at = new MapCoordinates(Vector2.Zero, map);
        return (SEntMan.SpawnEntity("MobHuman", at), at);
    }

    private EntityUid Powered(string prototype, MapCoordinates at)
    {
        var machine = SEntMan.SpawnEntity(prototype, at);
        SEntMan.GetComponent<ApcPowerReceiverComponent>(machine).Powered = true;
        return machine;
    }

    private void Raise(EntityUid machine, EntityUid actor, BoundUserInterfaceMessage message)
    {
        message.Actor = actor;
        SEntMan.EventBus.RaiseLocalEvent(machine, (object) message);
    }

    private bool Running(EntityUid user, EntityUid machine)
    {
        var comp = SEntMan.GetComponent<DoAfterComponent>(user);
        return comp.DoAfters.Values.Any(run => run.Args.Target == machine && !run.Cancelled && !run.Completed);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryLaboratoryMachineHasAWindow()
    {
        var (_, at) = World();
        foreach (var prototype in Machines)
        {
            var machine = SEntMan.SpawnEntity(prototype, at);
            Assert.That(SEntMan.HasComponent<NCLabMachineUiComponent>(machine), Is.True, $"{prototype} is operated through a window");
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StartingFromTheWindowRunsTheProcedureAndCancelStopsIt()
    {
        var (user, at) = World();
        var purifier = Powered("NCPharmaPurifier", at);
        var waste = SEntMan.SpawnEntity("NCPharmaWasteCanister", at);
        Assert.That(_slots.TryInsert(purifier, "waste", waste, user), Is.True);
        var canister = SEntMan.SpawnEntity("NCPharmaSourceCanisterStreet", at);
        Assert.That(_slots.TryInsert(purifier, "source", canister, user), Is.True);

        Raise(purifier, user, new NCLabMachineStartMessage());
        Assert.That(Running(user, purifier), Is.True, "the start button starts the purification");

        Raise(purifier, user, new NCLabMachineEjectMessage("source"));
        Assert.That(_slots.GetItemOrNull(purifier, "source"), Is.EqualTo(canister), "a machine at work keeps what is in it");

        Raise(purifier, user, new NCLabMachineCancelMessage());
        Assert.That(Running(user, purifier), Is.False, "cancel stops it");

        Raise(purifier, user, new NCLabMachineEjectMessage("source"));
        Assert.That(_slots.GetItemOrNull(purifier, "source"), Is.Null, "idle again, the slot is emptied from the window");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AnUnpoweredMachineDoesNotStart()
    {
        var (user, at) = World();
        var purifier = SEntMan.SpawnEntity("NCPharmaPurifier", at);
        SEntMan.GetComponent<ApcPowerReceiverComponent>(purifier).Powered = false;
        var canister = SEntMan.SpawnEntity("NCPharmaSourceCanisterStreet", at);
        _slots.TryInsert(purifier, "source", canister, user);
        Raise(purifier, user, new NCLabMachineStartMessage());
        Assert.That(Running(user, purifier), Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheIncineratorBurnsTheCanisterInItsSlotFromTheWindow()
    {
        var (user, at) = World();
        var bin = Powered("NCPharmaWasteBin", at);
        var canister = SEntMan.SpawnEntity("NCPharmaWasteCanister", at);
        Assert.That(_solutions.TryGetDrainableSolution(canister, out var soln, out _), Is.True);
        _solutions.TryAddReagent(soln!.Value, "NCPharmaWaste", 20f);
        Assert.That(_slots.TryInsert(bin, "canister", canister, user), Is.True);

        Raise(bin, user, new NCLabMachineStartMessage());
        Assert.That(_solutions.TryGetDrainableSolution(canister, out _, out var left), Is.True);
        Assert.That((float) left!.GetTotalPrototypeQuantity("NCPharmaWaste"), Is.EqualTo(0f).Within(0.01f), "the waste is burned");
    }
}
