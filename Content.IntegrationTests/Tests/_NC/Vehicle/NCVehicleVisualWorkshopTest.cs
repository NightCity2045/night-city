// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Content.Client._NC.Vehicle;
using Content.Client._NC.Vehicle.Workshop;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._NC.Vehicle;
using Content.Server._NC.Vehicle.Workshop;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using Content.Shared.Hands.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._NC.Vehicle;

#pragma warning disable RA0033 // Integration test uses literal prototype IDs for test chassis
[TestFixture]
public sealed class NCVehicleVisualWorkshopTest : InteractionTest
{
    private uint _sequence;

    [Test]
    public async Task TyreChangerSeparatesAndReassemblesThePhysicalWheelBundle()
    {
        var changer = await SpawnEntity("NCVehicleTyreChanger", SEntMan.GetCoordinates(TargetCoords));
        var wheel = await SpawnEntity("NCVehicleFrontLeftRimItem", SEntMan.GetCoordinates(PlayerCoords));
        var chassis = SProtoMan.Index<NCVehicleChassisPrototype>("NCVehicleDevelopmentSedan");
        var rim = chassis.Slots["fl-rim"].DefaultPart!.Value;
        var tyre = chassis.Slots["fl-tyre"].DefaultPart!.Value;
        var expectedMass = SProtoMan.Index(rim).Mass + SProtoMan.Index(tyre).Mass;
        await Server.WaitAssertion(() =>
        {
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(changer, new Vector2(0, -1)));
            var bundle = SEntMan.EnsureComponent<NCVehicleDetachedAssemblyComponent>(wheel);
            bundle.Chassis = chassis.ID;
            bundle.RootSlot = "fl-rim";
            bundle.Parts["fl-rim"] = new(rim, 1);
            bundle.Parts["fl-tyre"] = new(tyre, .65f);
            Server.System<NCVehiclePartPhysicsSystem>().TryRefresh(wheel);
            Assert.That(Server.System<ItemSlotsSystem>().TryInsert(changer,
                NCVehicleWorkbenchSystem.AssemblySlot, wheel, null), Is.True);
            Assert.That(SUiSys.TryOpenUi(changer, NCVehicleUiKey.Workbench, SPlayer), Is.True);
        });
        await RunTicks(5);
        Assert.That(GetWindow<NCVehicleWorkbenchWindow>(), Is.Not.Null);

        EntityUid removedTyre = default;
        await Server.WaitAssertion(() =>
        {
            var profile = SProtoMan.Index(chassis.Workshop!.Value);
            var node = profile.Nodes["fl-tyre"];
            var bundle = SEntMan.GetComponent<NCVehicleDetachedAssemblyComponent>(wheel);
            var request = new NCVehicleWorkbenchRequest("fl-tyre", node.Position + node.Size / 2,
                bundle.Revision, NCVehicleWorkbenchAction.Remove);
            var system = Server.System<NCVehicleWorkbenchSystem>();
            Assert.That(system.TryOperate((changer, SEntMan.GetComponent<NCVehicleWorkshopEquipmentComponent>(changer)),
                SPlayer, request, out var feedback), Is.True, feedback.ToString());
            Assert.That(bundle.Parts.Keys, Is.EquivalentTo(new[] { "fl-rim" }));
            removedTyre = HandSys.GetActiveItem(SPlayer)!.Value;
            Assert.That(SEntMan.GetComponent<NCVehiclePartItemComponent>(removedTyre).Condition, Is.EqualTo(.65f));
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(wheel).Mass,
                Is.EqualTo(SProtoMan.Index(rim).Mass).Within(.01));
        });

        await Server.WaitAssertion(() =>
        {
            var profile = SProtoMan.Index(chassis.Workshop!.Value);
            var node = profile.Nodes["fl-tyre"];
            var bundle = SEntMan.GetComponent<NCVehicleDetachedAssemblyComponent>(wheel);
            var request = new NCVehicleWorkbenchRequest("fl-tyre", node.Position + node.Size / 2,
                bundle.Revision, NCVehicleWorkbenchAction.Install);
            var system = Server.System<NCVehicleWorkbenchSystem>();
            Assert.That(system.TryOperate((changer, SEntMan.GetComponent<NCVehicleWorkshopEquipmentComponent>(changer)),
                SPlayer, request, out var feedback), Is.True, feedback.ToString());
            Assert.That(SEntMan.EntityExists(removedTyre), Is.False);
            Assert.That(bundle.Parts.Keys, Is.EquivalentTo(new[] { "fl-rim", "fl-tyre" }));
            Assert.That(bundle.Parts["fl-tyre"].Condition, Is.EqualTo(.65f));
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(wheel).Mass, Is.EqualTo(expectedMass).Within(.01));
        });

        var kitUid = await SpawnEntity("NCVehicleTyreRepairKit", SEntMan.GetCoordinates(PlayerCoords));
        await Hold(kitUid);
        await Server.WaitAssertion(() =>
        {
            SEntMan.GetComponent<NCVehicleRepairKitComponent>(kitUid).Seconds = .1f;
            var profile = SProtoMan.Index(chassis.Workshop!.Value);
            var node = profile.Nodes["fl-tyre"];
            var bundle = SEntMan.GetComponent<NCVehicleDetachedAssemblyComponent>(wheel);
            var request = new NCVehicleWorkbenchRequest("fl-tyre", node.Position + node.Size / 2,
                bundle.Revision, NCVehicleWorkbenchAction.Repair);
            var system = Server.System<NCVehicleWorkbenchSystem>();
            Assert.That(system.TryStart((changer, SEntMan.GetComponent<NCVehicleWorkshopEquipmentComponent>(changer)),
                SPlayer, request, out var feedback), Is.True, feedback.ToString());
        });
        await RunSeconds(1);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.EntityExists(kitUid), Is.False);
            Assert.That(SEntMan.GetComponent<NCVehicleDetachedAssemblyComponent>(wheel).Parts["fl-tyre"].Condition,
                Is.EqualTo(.9f).Within(.001));
            Assert.That(SEntMan.GetComponent<NCVehicleWorkbenchSessionComponent>(SPlayer).Pending, Is.False);
        });
    }

    [Test]
    public async Task VisualPortsDrainAndRefillTheSelectedRealCircuit()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        var pan = await SpawnEntity("NCVehicleFluidDrainPan", SEntMan.GetCoordinates(PlayerCoords));
        await Server.WaitAssertion(() =>
        {
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(car, new Vector2(-1.1f, 1.6f)));
            Assert.That(HandSys.TryPickup(SPlayer, pan), Is.True);
            var service = Server.System<NCVehicleServiceSystem>();
            Assert.That(service.TryStart(car, SPlayer, "", NCVehicleServiceAction.Hood), Is.True);
            Assert.That(service.TryOpen(car, SPlayer), Is.True);
        });
        await RunTicks(5);
        await Operate(car, "engine", "", NCVehicleWorkshopAction.Drain);
        await Server.WaitAssertion(() =>
        {
            var solution = Server.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(car, "oil", out _, out var oil), Is.True);
            Assert.That(solution.TryGetSolution(pan, "tank", out _, out var waste), Is.True);
            Assert.That(oil.Volume.Float(), Is.EqualTo(4000).Within(.01));
            Assert.That(waste.Volume.Float(), Is.EqualTo(1000).Within(.01));
            var state = Server.System<NCVehicleFluidsSystem>().GetWorkshopState(car).Single(x => x.Circuit == NCVehicleFluidCircuit.Oil);
            Assert.That(state.Fraction, Is.EqualTo(.8f).Within(.001));
            Assert.That(state.Purity, Is.EqualTo(1).Within(.001));
        });
        var bottle = await SpawnEntity("NCVehicleOilBottle", SEntMan.GetCoordinates(PlayerCoords));
        await Hold(bottle);
        await Operate(car, "engine", "", NCVehicleWorkshopAction.Fill);
        await Server.WaitAssertion(() =>
        {
            var solution = Server.System<SharedSolutionContainerSystem>();
            Assert.That(solution.TryGetSolution(car, "oil", out _, out var oil), Is.True);
            Assert.That(solution.TryGetSolution(bottle, "tank", out _, out var source), Is.True);
            Assert.That(oil.Volume.Float(), Is.EqualTo(5000).Within(.01));
            Assert.That(source.Volume.Float(), Is.EqualTo(0).Within(.01));
        });
    }

    [Test]
    public async Task WheelTransfersItsTyreWithoutLosingMassOrPermittingDuplicateRemoval()
    {
        try { await CheckWheel(); }
        catch (Exception error) { TestContext.Error.WriteLine(error); throw; }
    }

    private async Task CheckWheel()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        var liftCoords = SEntMan.GetCoordinates(PlayerCoords).Offset(new Vector2(-1, 1));
        await SetTile(Plating, SEntMan.GetNetCoordinates(liftCoords), MapData.Grid);
        await SpawnEntity("NCVehicleWorkshopLift", liftCoords);
        var wrench = await SpawnEntity("NCVehicleMechanicTool", SEntMan.GetCoordinates(PlayerCoords));
        float mass = 0;
        await Server.WaitAssertion(() =>
        {
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(car, new Vector2(-1.3f, 1.1f)));
            HandSys.AddHand(SPlayer, "hand_left", HandLocation.Left);
            Assert.That(HandSys.TryPickup(SPlayer, wrench), Is.True);
            var service = Server.System<NCVehicleServiceSystem>();
            Assert.That(service.TryStart(car, SPlayer, "", NCVehicleServiceAction.Lift), Is.True);
            Assert.That(service.TryOpen(car, SPlayer), Is.True);
            Assert.That(SEntMan.TryGetComponent<NCVehicleWorkshopSessionComponent>(SPlayer, out var session), Is.True, "Workshop session after open");
            Assert.That(session.Vehicle, Is.EqualTo(car));
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            mass = assembly.Profile.Mass;
            Assert.That(Server.System<NCVehicleAssemblySystem>().TrySetPart(car, "fl-pads", assembly.Installed["fl-pads"] with { Condition = .4f }), Is.True);
            Assert.That(assembly.Installed["fl-rim"].WorkPoints!.Count, Is.EqualTo(5));
            var forbidden = Request(car, "fl-pads", "", NCVehicleWorkshopAction.Remove);
            Assert.That(Server.System<NCVehicleWorkshopSystem>().CanOperate(car, SPlayer, forbidden, out var reason), Is.False);
            Assert.That(reason.ToString(), Is.EqualTo("nc-workshop-covered"));
        });
        await RunTicks(5);
        Assert.That(GetWindow<NCVehicleServiceWindow>(), Is.Not.Null);
        for (var i = 1; i <= 5; i++)
            await Operate(car, "fl-rim", "nut-" + i, NCVehicleWorkshopAction.Work);
        await Server.WaitAssertion(() =>
        {
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            Assert.That(assembly.Installed["fl-rim"].WorkPoints!.Values, Is.All.False);
            Assert.That(assembly.Installed["fr-rim"].Fastened, Is.True);
            Assert.That(HandSys.TryDrop(SPlayer, wrench), Is.True);
        });
        await Operate(car, "fl-rim", "", NCVehicleWorkshopAction.Remove);
        EntityUid wheel = default;
        await Server.WaitAssertion(() =>
        {
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            Assert.That(assembly.Installed.ContainsKey("fl-rim"), Is.False);
            Assert.That(assembly.Installed.ContainsKey("fl-tyre"), Is.False);
            Assert.That(assembly.Profile.Mass, Is.EqualTo(mass - 17).Within(.001));
            wheel = HandSys.GetActiveItem(SPlayer)!.Value;
            var bundle = SEntMan.GetComponent<NCVehicleDetachedAssemblyComponent>(wheel);
            Assert.That(bundle.Parts.Keys, Is.EquivalentTo(new[] { "fl-rim", "fl-tyre" }));
            Assert.That(bundle.Parts["fl-tyre"].Condition, Is.EqualTo(1));
            Assert.That(SEntMan.GetComponent<PhysicsComponent>(wheel).Mass, Is.EqualTo(17).Within(.01));
            var again = Request(car, "fl-rim", "", NCVehicleWorkshopAction.Remove);
            Assert.That(Server.System<NCVehicleWorkshopSystem>().TryStart(car, SPlayer, again), Is.False);
        });
        // Replace pads through their retaining point while the wheel is physically off the car.
        var pry = await SpawnEntity("Crowbar", SEntMan.GetCoordinates(PlayerCoords));
        await Hold(pry);
        await Operate(car, "fl-pads", "retaining-pin", NCVehicleWorkshopAction.Work);
        await Operate(car, "fl-pads", "", NCVehicleWorkshopAction.Remove);
        var pads = await SpawnEntity("NCVehicleFrontLeftPadsItem", SEntMan.GetCoordinates(PlayerCoords));
        await Hold(pads);
        await Operate(car, "fl-pads", "", NCVehicleWorkshopAction.Install);
        var fasteners = await SpawnEntity(("NCVehicleFasteners", 10), SEntMan.GetCoordinates(PlayerCoords));
        await Hold(pry, fasteners);
        await Operate(car, "fl-pads", "retaining-pin", NCVehicleWorkshopAction.Work);
        await Server.WaitAssertion(() =>
        {
            var drive = SEntMan.GetComponent<NCVehicleDriveComponent>(car);
            Assert.That(NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Braking, 0), Is.EqualTo(1));
        });
        await Hold(wheel);
        await Operate(car, "fl-rim", "", NCVehicleWorkshopAction.Install);
        await Server.WaitAssertion(() =>
        {
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            Assert.That(SEntMan.EntityExists(wheel), Is.False);
            Assert.That(assembly.Profile.Mass, Is.EqualTo(mass).Within(.001));
            Assert.That(assembly.Installed["fl-tyre"].Condition, Is.EqualTo(1));
            Assert.That(assembly.Installed["fl-rim"].Fastened, Is.False);
            Assert.That(assembly.ReservedSlots, Is.Empty);
        });
        var torqueWrench = await SpawnEntity("NCVehicleTorqueWrench", SEntMan.GetCoordinates(PlayerCoords));
        await Hold(torqueWrench, fasteners);
        for (var i = 1; i <= 5; i++)
            await Operate(car, "fl-rim", "nut-" + i, NCVehicleWorkshopAction.Work);
        await Server.WaitAssertion(() =>
        {
            var assembly = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car);
            Assert.That(assembly.Installed["fl-rim"].Fastened, Is.True);
            Assert.That(assembly.Installed["fl-rim"].PointTorque!.Values, Is.All.EqualTo(105));
            Assert.That(assembly.Installed["fl-pads"].Condition, Is.EqualTo(1));
            Assert.That(SEntMan.GetComponent<Content.Shared.Stacks.StackComponent>(fasteners).Count, Is.EqualTo(4));
        });
    }

    private async Task Hold(EntityUid active, EntityUid? other = null)
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var item in HandSys.EnumerateHeld(SPlayer).ToArray())
                Assert.That(HandSys.TryDrop(SPlayer, item), Is.True);
            HandSys.TrySetActiveHand(SPlayer, "hand_right");
            Assert.That(HandSys.TryPickup(SPlayer, active, "hand_right"), Is.True);
            if (other is { } second) Assert.That(HandSys.TryPickup(SPlayer, second, "hand_left"), Is.True);
        });
    }

    [Test]
    public async Task ActualClientPointClickDisconnectsOnlyOneBatteryTerminal()
    {
        try { await CheckClientPoint(); }
        catch (Exception error) { TestContext.Error.WriteLine(error); throw; }
    }

    private async Task CheckClientPoint()
    {
        var car = await SpawnEntity("NCToyotaSupra1996", SEntMan.GetCoordinates(TargetCoords));
        var tool = await SpawnEntity("NCVehicleMechanicTool", SEntMan.GetCoordinates(PlayerCoords));
        await Server.WaitAssertion(() =>
        {
            foreach (var chassis in SProtoMan.EnumeratePrototypes<NCVehicleChassisPrototype>())
                if (chassis.Workshop is { } profileId)
                    Assert.That(NCVehicleWorkshopGeometry.Valid(SProtoMan.Index(profileId), chassis), Is.True, chassis.ID);
            Transform.SetCoordinates(SPlayer, new EntityCoordinates(car, new Vector2(-1.1f, 1.6f)));
            Assert.That(HandSys.TryPickup(SPlayer, tool), Is.True);
            var service = Server.System<NCVehicleServiceSystem>();
            Assert.That(service.TryStart(car, SPlayer, "", NCVehicleServiceAction.Hood), Is.True);
            Assert.That(service.TryOpen(car, SPlayer), Is.True);
        });
        await RunTicks(5);
        var field = GetControlFromField<NCVehicleWorkshopField>("Field", GetWindow<NCVehicleServiceWindow>());
        var chassis = SProtoMan.Index<NCVehicleChassisPrototype>("NCVehicleDevelopmentSedan");
        var profile = SProtoMan.Index(chassis.Workshop!.Value);
        var node = profile.Nodes["battery"];
        var terminal = node.Position + chassis.Slots["battery"].WorkPoints["negative"].Position * node.Size;
        var size = Math.Max(1, Math.Min(field.PixelWidth, field.PixelHeight) - 20);
        var pixel = (field.PixelSize - new Vector2(size)) / 2 + terminal * size;
        var screen = new ScreenCoordinates(field.GlobalPixelPosition + pixel, field.Window!.Id);
        await Client.DoGuiEvent(field, new GUIBoundKeyEventArgs(EngineKeyFunctions.UIClick, BoundKeyState.Down,
            screen, true, pixel / field.UIScale, pixel));
        await RunSeconds(3);
        await Server.WaitAssertion(() =>
        {
            var battery = SEntMan.GetComponent<NCVehicleAssemblyComponent>(car).Installed["battery"];
            Assert.That(battery.WorkPoints!["negative"], Is.False);
            Assert.That(battery.WorkPoints["connection"], Is.True);
            Assert.That(battery.Fastened, Is.True);
            Assert.That(battery.Connected, Is.False);
            Assert.That(Server.System<NCVehicleConditionSystem>().CanStart(car, out _), Is.False);
        });
    }

    private NCVehicleWorkshopRequest Request(EntityUid car, string nodeId, string point, NCVehicleWorkshopAction action)
    {
        var chassis = SProtoMan.Index(SEntMan.GetComponent<NCVehicleComponent>(car).Chassis);
        var node = SProtoMan.Index(chassis.Workshop!.Value).Nodes[nodeId];
        var at = point.Length == 0 ? new Vector2(.5f) : chassis.Slots[node.Slot].WorkPoints[point].Position;
        return new(node.View, node.Position + at * node.Size, nodeId, point,
            SEntMan.GetComponent<NCVehicleAssemblyComponent>(car).SlotRevisions.GetValueOrDefault(node.Slot), ++_sequence, action);
    }

    private async Task Operate(EntityUid car, string node, string point, NCVehicleWorkshopAction action)
    {
        await Server.WaitAssertion(() =>
        {
            var system = Server.System<NCVehicleWorkshopSystem>();
            var request = Request(car, node, point, action);
            Assert.That(system.CanOperate(car, SPlayer, request, out var reason), Is.True, reason.ToString());
            var session = SEntMan.GetComponent<NCVehicleWorkshopSessionComponent>(SPlayer);
            Assert.That(system.TryStart(car, SPlayer, request), Is.True,
                $"{node}/{point}: seq {request.Sequence}/{session.Sequence}, pending {session.Pending}, open {SUiSys.IsUiOpen(car, NCVehicleUiKey.Service, SPlayer)}, feedback {session.Feedback}");
        });
        await RunSeconds(4);
        await Server.WaitAssertion(() => Assert.That(SEntMan.GetComponent<NCVehicleWorkshopSessionComponent>(SPlayer).Pending, Is.False));
    }
}
