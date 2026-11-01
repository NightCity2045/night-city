// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Surgery;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Buckle;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaLaboratoryClinicalTest
{
    private const string ProcedureProgram = """
        module native.lab; import meta.medical; import meta.system;
        link monitor:medical.MedicalMonitor="monitor";
        link procedure:medical.ProcedureController="procedure";
        var patient:option<medical.PatientRef>=none;
        fn main()->void{}
        pub fn capture()->void {
            match(monitor.patient()){
                ok(reference)=>{patient=some(reference);system.log("procedure-patient");}
                err(error)=>{system.log("procedure-patient-rejected");}
            }
        }
        pub async fn run()->void {
            match(patient){
                some(reference)=>{
                    match(await procedure.startSelectedStep(reference,"native-procedure")){
                        ok(receipt)=>{system.log("procedure-admitted");}
                        err(error)=>{system.log("procedure-rejected");}
                    }
                }
                none=>{system.log("procedure-no-patient");}
            }
        }
        """;

    [TestCase("apply")]
    [TestCase("move")]
    [TestCase("stop")]
    public async Task PublishedProcedureUsesRealHeldToolAndNativeDoAfterAndNeverAppliesAfterRevocation(string mode)
    {
        var scene = await CreateScene(clinicalHost: true);
        var controller = scene["bedside"]; var patient = scene["patient"]; var table = scene["table"];
        EntityUid region = default;
        float contaminationBefore = 0;
        TimeSpan stepDuration = default;
        try
        {
            await Server.WaitAssertion(() =>
            {
                var tableCoordinates = SComp<TransformComponent>(table).Coordinates;
                _transforms.SetCoordinates(controller, tableCoordinates.Offset(new Vector2(0, -1)));
                _transforms.SetCoordinates(patient, tableCoordinates);
                Assert.That(Server.System<SharedBuckleSystem>().TryBuckle(patient, patient, table, popup: false), Is.True);
                _transforms.SetCoordinates(scene.Actor, tableCoordinates.Offset(new Vector2(-0.3f, -0.7f)));
                Assert.That(_devices.CanManage(scene["host"], scene.Actor), Is.True);
                Assert.That(_clinical.TryBindPatient(controller, scene.Actor, patient), Is.True);
                Assert.That(_clinical.TrySetAutomation(controller, scene.Actor, true), Is.True);
                var cautery = SSpawnAtPosition("Cautery", SComp<TransformComponent>(scene.Actor).Coordinates);
                Assert.That(Server.System<SharedHandsSystem>().TryPickupAnyHand(scene.Actor, cautery), Is.True);
                Assert.That(Server.System<SharedHandsSystem>().TryGetActiveItem(scene.Actor, out var active) && active == cautery, Is.True);
                var implant = SSpawnAtPosition("NCImplantNeuralLinkMk1", tableCoordinates);
                Assert.That(Server.System<NCTargetingSelectionSystem>().TrySetSelection(scene.Actor,
                    new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneHead"), out _, out _), Is.True);
                Assert.That(Server.System<NCSurgeryInteractionSystem>().TryStageCyberware(scene.Actor, patient, implant), Is.True);
                Assert.That(Server.System<NCAnatomySystem>().TryGetOrgan(patient, "Head", out region), Is.True);
                contaminationBefore = SComp<NCSurgicalSiteComponent>(region).Contamination;
                stepDuration = Server.ResolveDependency<IPrototypeManager>().Index<NCSurgeryStepPrototype>("NCSurgeryStepPrepareField").Duration;
                // No direct patient/site/authorization mutations. The held production tool and strapped table supply them.
            });
            var program = await Install(scene, ProcedureProgram,
                new Dictionary<string, EntityUid> { ["monitor"] = controller, ["procedure"] = controller }, "native.lab.procedure");
            await Server.WaitPost(() => _transforms.SetCoordinates(scene.Actor,
                SComp<TransformComponent>(table).Coordinates.Offset(new Vector2(-0.3f, -0.7f))));
            var capture = await Invoke(scene, program, "capture");
            await Until(() => capture.Completed, "Procedure patient capture did not finish.");
            var start = await Invoke(scene, program, "run");
            await Until(() => STryComp<NCSurgeryGameplaySelectionComponent>(scene.Actor, out var selection) && selection.StepInProgress,
                "Actual native timed surgical step never started.");
            await Server.WaitAssertion(() =>
            {
                Assert.That(SComp<NCSurgerySessionComponent>(region).CurrentStepIndex, Is.Zero);
                Assert.That(SComp<NCSurgerySessionComponent>(region).History, Is.Empty);
                if (mode == "move") _transforms.SetCoordinates(scene.Actor,
                    SComp<TransformComponent>(scene.Actor).Coordinates.Offset(new Vector2(0.4f, 0)));
                else if (mode == "stop") _runtime.StopInstallation(scene["host"], program.Installation.Id);
            });
            var receipt = await Receipt<MetaOperationReceipt>(scene, controller, "meta.clinical.operation", "procedure",
                "native-procedure", value => value.Terminal);
            await AdvancePast(stepDuration + TimeSpan.FromSeconds(1));
            await Server.WaitAssertion(() =>
            {
                var session = SComp<NCSurgerySessionComponent>(region);
                var site = SComp<NCSurgicalSiteComponent>(region);
                Assert.That(receipt.Status, Is.EqualTo(mode == "apply" ? MetaOperationStatus.Applied :
                    mode == "move" ? MetaOperationStatus.Rejected : MetaOperationStatus.OutcomeUnknown));
                if (mode == "apply")
                {
                    Assert.That(start.Completed, Is.True);
                    Assert.That(session.History, Has.Count.EqualTo(1));
                    Assert.That(session.CurrentStepIndex, Is.EqualTo(1));
                    // No anesthetic is administered here: the real procedure's preparation thresholds remain unmet.
                    // This native compromised step applies its actual contamination effect, not imaginary healing.
                    Assert.That(session.History.Single().StepIndex, Is.Zero);
                    Assert.That(session.History.Single().Outcome, Is.EqualTo(NCSurgeryStepOutcome.Compromised));
                    Assert.That(site.Contamination, Is.EqualTo(Math.Clamp(contaminationBefore + 0.2f, 0, 1)).Within(0.0001f));
                    Assert.That(site.AccessDepth, Is.EqualTo(NCSurgicalAccessDepth.Closed));
                }
                else
                {
                    Assert.That(session.History, Is.Empty);
                    Assert.That(session.CurrentStepIndex, Is.Zero);
                    Assert.That(site.Contamination, Is.EqualTo(contaminationBefore));
                    Assert.That(site.AccessDepth, Is.EqualTo(NCSurgicalAccessDepth.Closed));
                }
            });
        }
        finally { await Finish(scene); }
    }
}
