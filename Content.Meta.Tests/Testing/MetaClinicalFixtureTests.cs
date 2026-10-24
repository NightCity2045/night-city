// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaClinicalFixtureTests
{
    [TestCase("Applied", "done")]
    [TestCase("Rejected", "rejected")]
    [TestCase("OutcomeUnknown", "reconcile")]
    public void PatientReferenceAndNativeReceiptStatusFlowThroughRealVm(string status, string expected)
    {
        const string source = """
            module test.clinical;
            import meta.medical;
            import meta.system;
            link monitor:medical.MedicalMonitor="bed";
            link controller:medical.DosingController="pump";
            async fn main()->void {
                match (monitor.patient()) {
                    ok(patient) => {
                        match (await controller.dose(patient,"dose-1")) {
                            ok(receipt) => {
                                match (receipt.status) {
                                    medical.OperationStatus.Prepared => {system.log("prepared");}
                                    medical.OperationStatus.Committing => {system.log("committing");}
                                    medical.OperationStatus.Running => {system.log("running");}
                                    medical.OperationStatus.Applied => {system.log("done");}
                                    medical.OperationStatus.Rejected => {system.log("rejected");}
                                    medical.OperationStatus.OutcomeUnknown => {system.log("reconcile");}
                                }
                            }
                            err(error) => {system.log("host-error");}
                        }
                    }
                    err(error) => {system.log("patient-error");}
                }
            }
            """;
        var compiled = new MetaCompiler().Compile(new([new("clinical.meta", source)], "test.clinical"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture("""
            {"rootModule":"test.clinical","responses":[
              {"member":"meta.medical.MedicalMonitor.patient","value":"patient-a"},
              {"member":"meta.medical.DosingController.dose","pending":true,"completeAfterSlices":3,
               "value":{"operationId":"receipt-a","status":"$STATUS","errorCode":{"tag":"none"},"stamp":{"epoch":1,"sequence":2,"tick":60}}}
            ],"events":[],"expectedLogs":["$EXPECTED"],"expectedCommands":["patient()","dose(patient-a,dose-1)"]}
            """.Replace("$STATUS", status, StringComparison.Ordinal).Replace("$EXPECTED", expected, StringComparison.Ordinal));
        var result = MetaTestHost.Run(artifact, fixture);
        Assert.That(result.Passed, Is.True, result.FailureCode + ":" + result.Fault);
    }

    [Test]
    public void AutomaticTransmissionContractsCompileWithExactTypedGearRequest()
    {
        const string source = """
            module test.tcu;
            import meta.vehicle;
            link gearbox:vehicle.TransmissionController="transmission";
            async fn main()->void {
                let state=gearbox.snapshot();
                let strategy=await gearbox.setStrategy(vehicle.TransmissionStrategy.Economy);
                let gear=await gearbox.requestGear(2);
            }
            """;
        var compiled = new MetaCompiler().Compile(new([new("tcu.meta", source)], "test.tcu"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotThrow(() => MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)));
        var bad = source.Replace("requestGear(2)", "requestGear(2.0)", StringComparison.Ordinal);
        var rejected = new MetaCompiler().Compile(new([new("bad.meta", bad)], "test.tcu"));
        Assert.That(rejected.Success, Is.False, "Forward gear IDs are integers, not an implicit float conversion.");
    }
}
