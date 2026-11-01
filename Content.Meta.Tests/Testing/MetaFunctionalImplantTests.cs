// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaFunctionalImplantTests
{
    [Test]
    public void ExplicitNativeModesAndOneShotPulseUseTheVerifiedCapabilityAbi()
    {
        var compiled = new MetaCompiler().Compile(new([new("function.meta", """
            module test.function; import meta.implants; import meta.system;
            link controller:FunctionalController="function";
            watch controller.stateChanged(state){if(!state.active){system.log("inactive");}}
            async fn main()->void{
                match(controller.snapshot()){ok(state)=>{if(state.kind==implants.FunctionKind.Tool){system.log("tool");}}err(error)=>{}}
                match(await controller.setEnabled(true)){ok(state)=>{if(state.active){system.log("enabled");}}err(error)=>{}}
                match(await controller.setEnabled(false)){ok(state)=>{if(!state.active){system.log("disabled");}}err(error)=>{}}
                match(await controller.pulse()){ok(state)=>{}err(error)=>{system.log("pulse rejected");}}
            }
            """)], "test.function"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        Assert.That(compiled.Module!.Manifest.RequiredScopes, Is.EquivalentTo(new[] { "implant.function.read", "implant.function.control", "implant.function.pulse", "implant.function.observe" }));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module));
        var fixture = MetaTestHost.ParseFixture("""
            {"rootModule":"test.function","responses":[
             {"member":"meta.implants.FunctionalController.snapshot","value":{"kind":"Tool","active":false,"operational":true,"cooldownMs":0,"activeRemainingMs":0,"stamp":{"epoch":1,"sequence":1,"tick":60}}},
             {"member":"meta.implants.FunctionalController.setEnabled","pending":true,"value":{"kind":"Tool","active":true,"operational":true,"cooldownMs":0,"activeRemainingMs":0,"stamp":{"epoch":1,"sequence":2,"tick":60}}},
             {"member":"meta.implants.FunctionalController.setEnabled","pending":true,"value":{"kind":"Tool","active":false,"operational":true,"cooldownMs":0,"activeRemainingMs":0,"stamp":{"epoch":1,"sequence":3,"tick":60}}},
             {"member":"meta.implants.FunctionalController.pulse","errorCode":"Unsupported"}],
             "events":[{"slot":"function","event":"stateChanged","sequence":1,"arguments":[{"kind":"Tool","active":false,"operational":true,"cooldownMs":0,"activeRemainingMs":0,"stamp":{"epoch":1,"sequence":4,"tick":60}}]}],
             "expectedLogs":["tool","enabled","disabled","pulse rejected","inactive"],"expectedCommands":["snapshot()","setEnabled(true)","setEnabled(false)","pulse()"]}
            """);
        var run = MetaTestHost.Run(artifact, fixture);
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }

    [Test]
    public void FactoryCatalogArtifactsStillVerifyAfterFunctionalCapabilitiesAreAdded()
    {
        var catalog = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.MachinesCatalogHash);
        Assert.That(catalog.Types.Any(t => t.Name == "meta.implants.FunctionalController"), Is.False);
        Assert.That(catalog.Types.Any(t => t.Name == "meta.system.JobReceipt"), Is.True);
        var compiled = new MetaCompiler(catalog).Compile(new([new("old.meta", "module test.old;import meta.system;fn main()->void{system.log(\"machines\");}")], "test.old"));
        Assert.That(compiled.Success, Is.True);
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        Assert.That(MetaTestHost.Run(artifact, new("test.old", [], [], ["machines"], [])).Passed, Is.True);
    }
}
