// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Server.Chemistry.EntitySystems;
using Content.Shared.Chemistry;
using Content.Shared.Bed.Sleep;
using Content.Shared.ActionBlocker;
using Content.Shared.FixedPoint;
using Content.Shared.Kitchen;
using Content.Shared.Kitchen.Components;
using Content.Shared.Kitchen.EntitySystems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaLaboratoryClinicalTest
{
    private const string GrindProgram = """
        module native.lab; import meta.lab; import meta.system;
        link grinder:lab.Grinder="grinder";
        fn main()->void{}
        pub async fn run()->void {
            match(await grinder.start(lab.GrinderProgram.Grind,"native-grind")){
                ok(receipt)=>{system.log("grinder-admitted");}
                err(error)=>{system.log("grinder-rejected");}
            }
        }
        """;

    [TestCase("apply")]
    [TestCase("manual")]
    [TestCase("power")]
    public async Task PublishedGrinderConsumesExactlyOnceAndNativeInterlocksPreventLaterOutput(string mode)
    {
        var scene = await CreateScene();
        var grinder = scene["grinder"]; var beaker = scene["beaker"]; var input = scene["ingredient"];
        var native = Server.System<SharedReagentGrinderSystem>();
        var expected = new Dictionary<string, FixedPoint2>();
        var formerWorkTime = TimeSpan.Zero;
        try
        {
            await Server.WaitAssertion(() =>
            {
                MoveNear(scene.Actor, grinder);
                var source = native.GetGrinderSolution(input, GrinderProgram.Grind);
                Assert.That(source, Is.Not.Null);
                foreach (var reagent in source!.Contents) expected[reagent.Reagent.Prototype] = source.GetTotalPrototypeQuantity(reagent.Reagent.Prototype);
                Assert.That(expected, Is.Not.Empty);
                var grinderState = SComp<ReagentGrinderComponent>(grinder);
                formerWorkTime = grinderState.WorkTime * grinderState.WorkTimeMultiplier;
                Assert.That(_slots.TryInsert(grinder, ReagentGrinderComponent.BeakerSlotId, beaker, scene.Actor), Is.True);
                Assert.That(Server.System<SharedContainerSystem>().Insert(input, SComp<ReagentGrinderComponent>(grinder).InputContainer), Is.True);
                MoveNear(scene.Actor, grinder); Assert.That(_machines.TrySetAutomation(grinder, scene.Actor, true), Is.True);
            });
            var program = await Install(scene, GrindProgram, new Dictionary<string, EntityUid> { ["grinder"] = grinder }, "native.lab.grinder");
            await Server.WaitPost(() => MoveBetween(scene, grinder));
            var invocation = await Invoke(scene, program, "run");
            await Until(() => native.IsActive(grinder), "The published META command never started the actual grinder.",
                diagnostics: () => $"Host powered={_devices.IsPowered(scene["host"])}; " +
                    $"grinder powered={_devices.IsPowered(grinder)}; supply={_batteries.GetCharge(scene.Physical.PowerSupply)}; " +
                    $"VM={program.Runtime.Process.State}; fault={program.Runtime.Process.Fault}; " +
                    $"stop={program.Runtime.Process.StopReason}; completed={invocation.Completed}; " +
                    $"logs={string.Join(" | ", _runtime.GetLogs(scene["host"], scene.Actor))}");
            if (mode != "apply") await Server.WaitAssertion(() =>
            {
                if (mode == "manual")
                {
                    MoveNear(scene.Actor, grinder);
                    Assert.That(_machines.TrySetAutomation(grinder, scene.Actor, false), Is.True);
                }
                else Server.System<SharedPowerReceiverSystem>().SetPowerDisabled(grinder, true);
            });
            if (mode == "apply") await Until(() => invocation.Completed && !native.IsActive(grinder), "Native grinder completion was not observed.");
            else await Until(() => !native.IsActive(grinder), "Native cancellation/power loss did not stop the grinder.");
            var receipt = await Receipt<MetaMachineReceipt>(scene, grinder, "meta.machine.operation", "grinder", "native-grind", value => value.Operation.Terminal);
            await AdvancePast(formerWorkTime + TimeSpan.FromSeconds(1)); // Cancellation must not produce output after its real former finish time.
            await Server.WaitAssertion(() =>
            {
                Assert.That(_solutions.TryGetFitsInDispenser(beaker, out _, out var result), Is.True);
                if (mode == "apply")
                {
                    Assert.That(SEntMan.EntityExists(input), Is.False);
                    Assert.That(receipt.Operation.Status, Is.EqualTo(MetaOperationStatus.Applied));
                    Assert.That(receipt.Requested, Is.EqualTo(1)); Assert.That(receipt.Completed, Is.EqualTo(1));
                    foreach (var (reagent, amount) in expected) Assert.That(result!.GetTotalPrototypeQuantity(reagent), Is.EqualTo(amount));
                    Assert.That(result!.Volume, Is.EqualTo(expected.Values.Aggregate(FixedPoint2.Zero, (left, right) => left + right)));
                }
                else
                {
                    Assert.That(SEntMan.EntityExists(input), Is.True);
                    Assert.That(SComp<ReagentGrinderComponent>(grinder).InputContainer.ContainedEntities.Contains(input), Is.True);
                    Assert.That(result!.Volume, Is.EqualTo(FixedPoint2.Zero));
                    Assert.That(receipt.Operation.Status, Is.EqualTo(MetaOperationStatus.Rejected)); Assert.That(receipt.Completed, Is.Zero);
                }
            });
        }
        finally { await Finish(scene); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublishedChemMasterReceiptMatchesRealPillsOrBottleAndExactBufferDebit(bool bottle)
    {
        var scene = await CreateScene(); var machine = scene["chemMaster"]; EntityUid output = default;
        try
        {
            await Server.WaitAssertion(() =>
            {
                MoveNear(scene.Actor, machine);
                output = SSpawnAtPosition(bottle ? "ChemistryEmptyBottle01" : "PillCanister", SComp<TransformComponent>(machine).Coordinates);
                Assert.That(_slots.TryInsert(machine, SharedChemMaster.OutputSlotName, output, scene.Actor), Is.True);
                AddWater(machine, SharedChemMaster.BufferSolutionName, 20);
                MoveNear(scene.Actor, machine); Assert.That(_machines.TrySetAutomation(machine, scene.Actor, true), Is.True);
            });
            var action = bottle ? "bottle(7,\"Native batch\",\"native-package\")" : "pills(3,5,\"Native batch\",\"native-package\")";
            var source = """
                module native.lab; import meta.lab; import meta.system;
                link packager:lab.ChemMaster="packager"; fn main()->void{}
                pub async fn run()->void {
                    match(await packager.$ACTION$){
                        ok(receipt)=>{if(receipt.status==system.OperationStatus.Applied){system.log("packaging-applied");}}
                        err(error)=>{system.log("packaging-rejected");}
                    }
                }
                """.Replace("$ACTION$", action, StringComparison.Ordinal);
            var program = await Install(scene, source, new Dictionary<string, EntityUid> { ["packager"] = machine }, "native.lab.packaging");
            // The practitioner remains physically beside both the host and ChemMaster, as required by the native packaging API.
            await Server.WaitPost(() => MoveBetween(scene, machine));
            var invocation = await Invoke(scene, program, "run");
            await Until(() => invocation.Completed, "The actual ChemMaster invocation did not complete.");
            var receipt = await Receipt<MetaMachineReceipt>(scene, machine, "meta.machine.operation", "chemmaster", "native-package", value => value.Operation.Terminal);
            await Server.WaitAssertion(() =>
            {
                Assert.That(receipt.Operation.Status, Is.EqualTo(MetaOperationStatus.Applied));
                Assert.That(receipt.Requested, Is.EqualTo(bottle ? 1 : 3)); Assert.That(receipt.Completed, Is.EqualTo(bottle ? 1 : 3));
                Assert.That(Solution(machine, SharedChemMaster.BufferSolutionName).GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(bottle ? 13 : 5)));
                if (bottle) Assert.That(Solution(output, SharedChemMaster.BottleSolutionName).GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(7)));
                else
                {
                    var pills = SComp<StorageComponent>(output).Container.ContainedEntities.ToArray();
                    Assert.That(pills, Has.Length.EqualTo(3));
                    foreach (var pill in pills) Assert.That(Solution(pill, SharedChemMaster.PillSolutionName).GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(5)));
                }
            });
        }
        finally { await Finish(scene); }
    }

    [Test]
    public async Task NativeChemMasterRejectsDoseAndPractitionerFailuresWithoutConsumingBuffer()
    {
        var scene = await CreateScene(); var machine = scene["chemMaster"];
        try
        {
            await Server.WaitAssertion(() =>
            {
                MoveNear(scene.Actor, machine);
                var output = SSpawnAtPosition("PillCanister", SComp<TransformComponent>(machine).Coordinates);
                Assert.That(_slots.TryInsert(machine, SharedChemMaster.OutputSlotName, output, scene.Actor), Is.True);
                AddWater(machine, SharedChemMaster.BufferSolutionName, 100); MoveNear(scene.Actor, machine);
                var native = Server.System<ChemMasterSystem>();
                Assert.That(native.TryCreatePills(machine, scene.Actor, 3, 21, "too much", () => true), Is.EqualTo(new NCChemPackagingOutcome(0, false)));
                Assert.That(native.TryCreatePills(machine, scene.Actor, 3, 5, "revoked", () => false), Is.EqualTo(new NCChemPackagingOutcome(0, false)));
                _transforms.SetCoordinates(scene.Actor, SComp<TransformComponent>(machine).Coordinates.Offset(new System.Numerics.Vector2(8, 0)));
                Assert.That(native.TryCreatePills(machine, scene.Actor, 3, 5, "distant", () => true), Is.EqualTo(new NCChemPackagingOutcome(0, false)));
                MoveNear(scene.Actor, machine);
                Assert.That(Server.System<SleepingSystem>().TrySleeping(scene.Actor), Is.True);
                Assert.That(Server.System<ActionBlockerSystem>().CanConsciouslyPerformAction(scene.Actor), Is.False);
                Assert.That(native.TryCreatePills(machine, scene.Actor, 3, 5, "unconscious", () => true), Is.EqualTo(new NCChemPackagingOutcome(0, false)));
                Assert.That(Solution(machine, SharedChemMaster.BufferSolutionName).GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(100)));
                Assert.That(SComp<StorageComponent>(output).Container.ContainedEntities, Is.Empty);
            });
        }
        finally { await Finish(scene); }
    }
}
