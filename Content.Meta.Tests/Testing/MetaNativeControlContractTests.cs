// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaNativeControlContractTests
{
    private static JsonElement Value(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static void Run(string module, string source, MetaTestFixture fixture)
    {
        var result = new MetaCompiler().Compile(new([new("control.meta", source)], module));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        var artifact = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        var run = MetaTestHost.Run(artifact, fixture);
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }

    [Test]
    public void AssistanceSnapshotAndTypedModeCommandCrossTheVerifiedVmBoundary()
    {
        const string source = """
            module test.assistance; import meta.vehicle; import meta.system;
            link assists:vehicle.DriverAssistance="assists";
            async fn main()->void {
                match(assists.snapshot()) {
                    ok(state)=>{if(state.absSupported){
                        match(await assists.setEnabled(vehicle.AssistanceSystem.Abs,true)) {
                            ok(changed)=>{if(changed.absEnabled){system.log("abs-enabled");}}
                            err(error)=>{system.log("rejected");}
                        }
                    }}
                    err(error)=>{system.log("unavailable");}
                }
            }
            """;
        var state = Value("""
            {"absSupported":true,"absEnabled":true,"stabilitySupported":false,"stabilityEnabled":false,
             "tractionSupported":true,"tractionEnabled":true,"longitudinalSlip":0.1,"lateralSlip":0.2,
             "wheelAngularSpeedsRadS":[10,10,11,11],"stamp":{"epoch":1,"sequence":1,"tick":60}}
            """);
        Run("test.assistance", source, new("test.assistance",
            [new("meta.vehicle.DriverAssistance.snapshot", state), new("meta.vehicle.DriverAssistance.setEnabled", state, Pending: true, CompleteAfterSlices: 3)],
            [], ["abs-enabled"], ["snapshot()", "setEnabled(Abs,true)"]));
    }

    [Test]
    public void DetailedNavigationReportsNoPathWithoutChangingTheNormativeLegacyEnum()
    {
        const string source = """
            module test.navigation; import meta.drone; import meta.system;
            link controller:drone.NavigationSupervisor="supervisor";
            watch controller.navigationOutcome(event) {
                match(event.outcome) {
                    drone.NavigationOutcome.None=>{system.log("none");}
                    drone.NavigationOutcome.Moving=>{system.log("moving");}
                    drone.NavigationOutcome.Arrived=>{system.log("arrived");}
                    drone.NavigationOutcome.Blocked=>{system.log("blocked");}
                    drone.NavigationOutcome.Cancelled=>{system.log("cancelled");}
                    drone.NavigationOutcome.NoPath=>{system.log("no-path");}
                }
            }
            fn main()->void {}
            """;
        Run("test.navigation", source, new("test.navigation", [],
            [new("supervisor", "navigationOutcome", [Value("""{"requestId":7,"outcome":"NoPath","stamp":{"epoch":1,"sequence":2,"tick":60}}""")], 1)],
            ["no-path"], []));
        Assert.That(MetaApiCatalog.Default.Types.Single(t => t.Name == "meta.drone.NavigationStatus").Variants,
            Is.EqualTo(new[] { "Arrived", "Blocked", "Cancelled" }));
    }

    [Test]
    public void SecurityAcceptanceRemainsPendingUntilTheActualPhysicalStateEvent()
    {
        const string source = """
            module test.security; import meta.vehicle; import meta.system;
            link security:vehicle.VehicleSecurity="security";
            watch security.stateChanged(state) {if(state.armed&&!state.pending){system.log("armed");}}
            async fn main()->void {
                match(await security.setArmed(true)) {
                    ok(state)=>{if(state.pending&&!state.armed){system.log("pending");}}
                    err(error)=>{system.log("rejected");}
                }
            }
            """;
        Run("test.security", source, new("test.security",
            [new("meta.vehicle.VehicleSecurity.setArmed", Value("""{"armed":false,"locked":false,"alarmActive":false,"pending":true,"stamp":{"epoch":1,"sequence":1,"tick":60}}"""))],
            [new("security", "stateChanged", [Value("""{"armed":true,"locked":true,"alarmActive":false,"pending":false,"stamp":{"epoch":1,"sequence":2,"tick":60}}""")], 1)],
            ["pending", "armed"], ["setArmed(true)"]));
    }
}
