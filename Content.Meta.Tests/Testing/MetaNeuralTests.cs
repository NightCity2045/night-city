// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;
namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaNeuralTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void BasicProgramExecutesRequestsAndThermalCutoff(bool accepted)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Resources", "Meta", "Firmware"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Resources", "Meta", "Firmware", "neural-basic.meta"));
        var compiled = new MetaCompiler().Compile(new([new("neural-basic.meta", source)], "neural.basic"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture("""
        {"rootModule":"neural.basic","responses":[
          {"member":"meta.implants.NeuralController.setEnabled","pending":true,"value":ACCEPTED},
          {"member":"meta.implants.NeuralController.setEnabled","pending":true,"value":true}],
         "events":[
          {"slot":"neural","event":"commanded","sequence":1,"arguments":[{"enabled":true,"stamp":{"epoch":1,"sequence":1,"tick":60}}]},
          {"slot":"neural","event":"sampled","sequence":2,"arguments":[{"kind":4,"enabled":true,"heat":0.9,"cooldownMs":30000,"stamp":{"epoch":1,"sequence":2,"tick":60}}]}],
         "expectedCommands":["setEnabled(true)","setEnabled(false)"],
         "expectedLogs":["Neural basic ready"EXTRALOG]}
        """.Replace("ACCEPTED", accepted.ToString().ToLowerInvariant()).Replace("EXTRALOG", accepted ? "" : ",\"Neural command unavailable\""));
        var result = MetaTestHost.Run(artifact, fixture);
        Assert.That(result.Passed, Is.True, result.FailureCode + ":" + result.Fault);
    }

    [Test]
    public void ExistingOpticsCatalogRemainsTrusted()
    {
        var previous = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreNeuralCatalogHash);
        Assert.That(previous.Types.Any(type => type.Name == "meta.implants.CyberEye"), Is.True);
        Assert.That(previous.Types.Any(type => type.Name == "meta.implants.NeuralController"), Is.False);
        var compilation = new MetaCompiler(previous).Compile(new([new("old.meta", "module old.main;fn main()->void{}")], "old.main"));
        Assert.DoesNotThrow(() => MetaVerifier.Verify(compilation.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compilation.Module!)));
    }
}
