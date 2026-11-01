// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server.DeviceLinking.Systems;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared.ActionBlocker;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaLaboratoryClinicalTest
{
    private const string DoseProgram = """
        module native.lab; import meta.medical; import meta.system;
        link monitor:medical.MedicalMonitor="monitor";
        link pump:medical.DosingController="pump";
        var patient:option<medical.PatientRef>=none;
        fn main()->void{}
        pub fn capture()->void {
            match(monitor.patient()){
                ok(reference)=>{patient=some(reference);system.log("patient-captured");}
                err(error)=>{system.log("patient-rejected");}
            }
        }
        pub async fn dose()->void {
            match(patient){
                some(reference)=>{
                    match(await pump.dose(reference,"native-dose")){
                        ok(receipt)=>{if(receipt.status==medical.OperationStatus.Applied){system.log("dose-applied");}}
                        err(error)=>{system.log("dose-rejected");}
                    }
                }
                none=>{system.log("no-patient");}
            }
        }
        """;

    [Test]
    public async Task PublishedDosingConsumesActualReservoirOnceAndItsNativeReceiptCannotDoseAgainOnRetry()
    {
        var scene = await CreateScene(clinicalHost: true); var controller = scene["bedside"]; var patient = scene["patient"];
        NCMetaDoseObservationComponent observation = null!;
        try
        {
            await Server.WaitAssertion(() =>
            {
                _transforms.SetCoordinates(patient, SComp<TransformComponent>(controller).Coordinates.Offset(new Vector2(0, 0.6f)));
                MoveNear(scene.Actor, controller);
                Assert.That(Server.System<ActionBlockerSystem>().CanConsciouslyPerformAction(scene.Actor), Is.True);
                Assert.That(_clinical.TryBindPatient(controller, scene.Actor, patient), Is.True);
                Assert.That(_clinical.TrySetAutomation(controller, scene.Actor, true), Is.True);
                AddWater(controller, "chemicals", 10);
                Assert.That(_solutions.TryGetInjectableSolution(patient, out var destinationEntity, out var destination), Is.True);
                Assert.That(destination!.AvailableVolume, Is.GreaterThan(FixedPoint2.New(1)));
                observation = SEntMan.EnsureComponent<NCMetaDoseObservationComponent>(patient);
                observation.Solution = destinationEntity!.Value.Owner;
                observation.Previous = destination.GetTotalPrototypeQuantity("Water");
            });
            var program = await Install(scene, DoseProgram, new Dictionary<string, EntityUid> { ["monitor"] = controller, ["pump"] = controller }, "native.lab.dosing");
            await Server.WaitPost(() => MoveBetween(scene, controller));
            var capture = await Invoke(scene, program, "capture");
            await Until(() => capture.Completed, "Native patient reference capture did not complete.");
            var dose = await Invoke(scene, program, "dose");
            await Until(() => dose.Completed, "Native dosing did not complete.");
            var receipt = await Receipt<MetaOperationReceipt>(scene, controller, "meta.clinical.operation", "dose", "native-dose", value => value.Terminal);
            await Server.WaitAssertion(() =>
            {
                Assert.That(receipt.Status, Is.EqualTo(MetaOperationStatus.Applied));
                Assert.That(Solution(controller, "chemicals").GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(9)));
                Assert.That(observation.Received, Is.EqualTo(FixedPoint2.New(1)), "The real patient's injectable solution must receive the exact physical dose.");
                Assert.That(observation.Increases, Is.EqualTo(1));
                Assert.That(_runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id), Does.Contain("patient-captured").And.Contain("dose-applied"));
            });
            await AdvancePast(TimeSpan.FromSeconds(1)); // Respect the same native command rate limiter on a real retry.
            var retry = await Invoke(scene, program, "dose");
            await Until(() => retry.Completed, "Exact dosing retry did not return its native receipt.");
            var repeated = await Receipt<MetaOperationReceipt>(scene, controller, "meta.clinical.operation", "dose", "native-dose", value => value.Terminal);
            await Server.WaitAssertion(() =>
            {
                Assert.That(repeated, Is.EqualTo(receipt));
                Assert.That(_runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id).Count(value => value == "dose-applied"), Is.EqualTo(2),
                    "The retry must return the genuine Applied receipt, rather than merely rejecting under the command rate limit.");
                Assert.That(Solution(controller, "chemicals").GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(9)), "A durable Applied receipt must not reapply the physical dose.");
                Assert.That(observation.Received, Is.EqualTo(FixedPoint2.New(1)));
                Assert.That(observation.Increases, Is.EqualTo(1));
            });
        }
        finally { await Finish(scene); }
    }

    [TestCase("sleep")]
    [TestCase("distant")]
    [TestCase("patient")]
    [TestCase("stop")]
    public async Task CapturedPatientAndPractitionerInterlocksNeverConsumeARevokedDose(string interlock)
    {
        var scene = await CreateScene(clinicalHost: true); var controller = scene["bedside"]; var patient = scene["patient"];
        try
        {
            await Server.WaitAssertion(() =>
            {
                _transforms.SetCoordinates(patient, SComp<TransformComponent>(controller).Coordinates.Offset(new Vector2(0, 0.6f)));
                MoveNear(scene.Actor, controller);
                Assert.That(_clinical.TryBindPatient(controller, scene.Actor, patient), Is.True);
                Assert.That(_clinical.TrySetAutomation(controller, scene.Actor, true), Is.True);
                AddWater(controller, "chemicals", 10);
            });
            var program = await Install(scene, DoseProgram, new Dictionary<string, EntityUid> { ["monitor"] = controller, ["pump"] = controller }, "native.lab.dosing-interlock");
            await Server.WaitPost(() => MoveBetween(scene, controller));
            var capture = await Invoke(scene, program, "capture");
            await Until(() => capture.Completed, "Native patient reference was not captured before revocation.");
            await Server.WaitAssertion(() => Assert.That(_runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id), Does.Contain("patient-captured")));
            await Server.WaitAssertion(() =>
            {
                if (interlock == "sleep")
                {
                    Assert.That(Server.System<SleepingSystem>().TrySleeping(scene.Actor), Is.True);
                    Assert.That(Server.System<ActionBlockerSystem>().CanConsciouslyPerformAction(scene.Actor), Is.False);
                }
                else if (interlock == "distant")
                {
                    // Still within the source terminal's management range, but outside the real bedside/practitioner range.
                    _transforms.SetCoordinates(scene.Actor, SComp<TransformComponent>(scene["host"]).Coordinates.Offset(new Vector2(1.4f, 0)));
                    Assert.That(_devices.CanManage(scene["host"], scene.Actor), Is.True);
                }
                else if (interlock == "patient")
                {
                    MoveNear(scene.Actor, controller);
                    var replacement = SSpawnAtPosition("MobHuman", SComp<TransformComponent>(patient).Coordinates);
                    Assert.That(_clinical.TryBindPatient(controller, scene.Actor, replacement), Is.True);
                    MoveBetween(scene, controller);
                }
                else _runtime.StopInstallation(scene["host"], program.Installation.Id);
            });
            if (interlock is "stop" or "patient")
            {
                await Until(() => program.Runtime.Process.State == MetaProcessState.Stopped, "Native stop/patient-context replacement did not revoke the exact VM generation.");
                await Server.WaitAssertion(() => Assert.That(_runtime.TryGetGeneration(scene["host"], scene.Owner, program.Runtime.Generation, out _), Is.False));
            }
            else
            {
                var dose = await Invoke(scene, program, "dose");
                await Until(() => dose.Completed, "The rejected clinical command did not finish.");
            }
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() => Assert.That(Solution(controller, "chemicals").GetTotalPrototypeQuantity("Water"), Is.EqualTo(FixedPoint2.New(10))));
        }
        finally { await Finish(scene); }
    }

    [Test]
    public async Task StationaryBridgeUsesOnlyTheWiredTableOccupantAndItsRealFilteredPoweredCapability()
    {
        var scene = await CreateScene(clinicalHost: true); var scanner = scene["scanner"]; var patient = scene["patient"]; var table = scene["table"];
        var diagnostics = Server.System<NCDiagnosticsSystem>();
        var buckle = Server.System<SharedBuckleSystem>();
        EntityUid gateway = default;
        EntityUid replacement = default;
        try
        {
            await Server.WaitAssertion(() =>
            {
                gateway = SSpawnAtPosition("NCMetaScannerGateway", SComp<TransformComponent>(scanner).Coordinates.Offset(new Vector2(0, 0.5f)));
                ChargePortable(gateway);
                var cable = SSpawnAtPosition("NCMetaNetworkCable", SComp<TransformComponent>(scanner).Coordinates);
                MoveNear(scene.Actor, scanner);
                Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, scene.Actor, scene["host"]), Is.True);
                Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, scene.Actor, gateway), Is.True);
                Assert.That(diagnostics.CanObserveStationaryScanner(scanner, patient, NCDiagnosticTier.ScannerInternal), Is.False, "An unbuckled body cannot become a scanner patient.");
                Server.System<DeviceLinkSystem>().LinkDefaults(null, scanner, table);
                _transforms.SetCoordinates(patient, SComp<TransformComponent>(table).Coordinates);
                Assert.That(buckle.TryBuckle(patient, patient, table, popup: false), Is.True);
            });
            await Claim(scene, gateway);
            await Server.WaitAssertion(() =>
            {
                MoveNear(scene.Actor, gateway);
                Assert.That(_clinical.TryBindPatient(gateway, scene.Actor, patient), Is.True);
                Assert.That(_clinical.TryBindStationaryScanner(gateway, scene.Actor, scanner), Is.True);
                Assert.That(diagnostics.TryBuildForStationaryScanner(scanner, patient, NCDiagnosticTier.ScannerInternal, out var report, out _), Is.True);
                Assert.That(report.Tier, Is.EqualTo(NCDiagnosticTier.ScannerInternal));
                Assert.That(report.Organs, Is.Not.Empty); Assert.That(report.Vitals.InternalBleedMilliunits, Is.Not.Null);
                Assert.That(diagnostics.TryBuildForStationaryScanner(scanner, patient, NCDiagnosticTier.AdminExact, out _, out var denied), Is.False);
                Assert.That(denied, Is.EqualTo(NCDiagnosticAccessFailure.AdminServerOnly));
            });
            var program = await Install(scene, StationaryProgram, new Dictionary<string, EntityUid> { ["monitor"] = gateway }, "native.lab.stationary");
            await Server.WaitPost(() => MoveNear(scene.Actor, gateway));
            var capture = await Invoke(scene, program, "capture");
            await Until(() => capture.Completed, "Stationary patient reference was not captured.");
            var read = await Invoke(scene, program, "scan");
            await Until(() => read.Completed, "Published stationary scanner read did not finish.");
            await Server.WaitAssertion(() => Assert.That(_runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id), Does.Contain("stationary-report")));
            await Server.WaitAssertion(() =>
            {
                // A retained console binding is invalid once its exact native scanner generation changes.
                Assert.That(_devices.TryInvalidate(scanner), Is.True);
                Assert.That(_runtime.TryGetGeneration(scene["host"], scene.Owner, program.Runtime.Generation, out _), Is.True,
                    "Only the scanner proof is revoked; this old gateway VM and its PatientRef must still exist for the regression.");
            });
            var stale = await Invoke(scene, program, "scan");
            await Until(() => stale.Completed, "Stale scanner generation did not complete its rejected read.");
            await Server.WaitAssertion(() =>
            {
                var logs = _runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id);
                Assert.That(logs, Does.Contain("stationary-rejected"));
                Assert.That(logs.Count(value => value == "stationary-report"), Is.EqualTo(1));
                MoveNear(scene.Actor, gateway);
                Assert.That(_clinical.TryBindStationaryScanner(gateway, scene.Actor, scanner), Is.True);
                Assert.That(program.Runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped), "Recommissioning revokes old gateway authority instead of resurrecting its saved reference.");
            });
            var restored = await Install(scene, StationaryProgram, new Dictionary<string, EntityUid> { ["monitor"] = gateway }, "native.lab.stationary");
            await Server.WaitPost(() => MoveNear(scene.Actor, gateway));
            var recapture = await Invoke(scene, restored, "capture");
            await Until(() => recapture.Completed, "Recommissioned scanner did not admit a new native patient reference.");
            var reread = await Invoke(scene, restored, "scan");
            await Until(() => reread.Completed, "Recommissioned scanner did not restore the actual published read.");
            await Server.WaitAssertion(() =>
            {
                Assert.That(restored.Runtime.Generation, Is.Not.EqualTo(program.Runtime.Generation));
                Assert.That(_runtime.GetLogs(scene["host"], scene.Actor, restored.Installation.Id), Does.Contain("stationary-report"));
                buckle.Unbuckle((patient, SComp<BuckleComponent>(patient)), null);
                Assert.That(diagnostics.CanObserveStationaryScanner(scanner, patient, NCDiagnosticTier.ScannerInternal), Is.False);
                replacement = SSpawnAtPosition("MobHuman", SComp<TransformComponent>(table).Coordinates);
                Assert.That(buckle.TryBuckle(replacement, replacement, table, popup: false), Is.True);
                Assert.That(diagnostics.CanObserveStationaryScanner(scanner, patient, NCDiagnosticTier.ScannerInternal), Is.False);
                Assert.That(diagnostics.CanObserveStationaryScanner(scanner, replacement, NCDiagnosticTier.ScannerInternal), Is.True);
                // Move the real strapped occupant beyond range while the scanner retains its physical power connection.
                var original = SComp<TransformComponent>(table).Coordinates;
                _transforms.Unanchor(table);
                _transforms.SetCoordinates(table, original.Offset(new Vector2(5, 0)));
                Assert.That(diagnostics.TryBuildForStationaryScanner(scanner, replacement, NCDiagnosticTier.ScannerInternal, out _, out var distant), Is.False);
                Assert.That(distant, Is.EqualTo(NCDiagnosticAccessFailure.OutOfRange));
                _transforms.SetCoordinates(table, original);
                Assert.That(diagnostics.CanObserveStationaryScanner(scanner, replacement, NCDiagnosticTier.ScannerInternal), Is.True);
                Server.System<SharedPowerReceiverSystem>().SetPowerDisabled(scanner, true);
            });
            await Until(() => !_devices.IsPowered(scanner), "The real scanner did not lose physical power.");
            await Server.WaitAssertion(() =>
            {
                Assert.That(diagnostics.TryBuildForStationaryScanner(scanner, replacement, NCDiagnosticTier.ScannerInternal, out _, out var unpowered), Is.False);
                Assert.That(unpowered, Is.EqualTo(NCDiagnosticAccessFailure.ToolUnpowered));
                Assert.That(_clinical.TryBindStationaryScanner(gateway, scene.Actor, scanner), Is.False);
            });
        }
        finally
        {
            // The gateway was spawned on the scene's real grid and is destroyed with that owned map.
            await Finish(scene);
        }
    }

    private const string StationaryProgram = """
        module native.lab; import meta.medical; import meta.system;
        link monitor:medical.MedicalMonitor="monitor"; fn main()->void{}
        var captured:option<medical.PatientRef>=none;
        pub fn capture()->void {
            match(monitor.patient()){
                ok(patient)=>{captured=some(patient);}
                err(error)=>{system.log("stationary-capture-rejected");}
            }
        }
        pub fn scan()->void {
            match(captured){
                some(patient)=>{
                    match(monitor.snapshot(patient)){
                        ok(report)=>{system.log("stationary-report");}
                        err(error)=>{system.log("stationary-rejected");}
                    }
                }
                none=>{system.log("stationary-rejected");}
            }
        }
        """;
}
