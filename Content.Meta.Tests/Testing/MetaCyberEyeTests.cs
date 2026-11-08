// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaCyberEyeTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void FactoryProgramActuallyHandlesCommandsAndRefreshesLease(bool accepted)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Resources", "Meta", "Firmware"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Resources", "Meta", "Firmware", "optic-basic.meta"));
        var compiled = new MetaCompiler().Compile(new([new("optic-basic.meta", source)], "optics.basic"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture("""
        {"rootModule":"optics.basic","responses":[
          {"member":"meta.implants.CyberEye.setMode","pending":true,"value":ACCEPTED},
          {"member":"meta.implants.CyberEye.heartbeat","pending":true,"value":true}],
         "events":[
          {"slot":"eye","event":"commanded","sequence":1,"arguments":[{"mode":7,"enabled":true,"stamp":{"epoch":1,"sequence":1,"tick":60}}]},
          {"slot":"eye","event":"sampled","sequence":2,"arguments":[{"light":0.1,"modules":128,"stamp":{"epoch":1,"sequence":2,"tick":60}}]}],
         "expectedCommands":["setMode(7,true)","heartbeat()"],
         "expectedLogs":["Optics basic ready"EXTRALOG]}
        """.Replace("ACCEPTED", accepted.ToString().ToLowerInvariant()).Replace("EXTRALOG", accepted ? "" : ",\"Module unavailable\""));
        var result = MetaTestHost.Run(artifact, fixture);
        Assert.That(result.Passed, Is.True, result.FailureCode + ":" + result.Fault);
    }

    [Test]
    public void ExistingMedicalFirmwareStillVerifies()
    {
        var previous = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreOpticCatalogHash);
        Assert.That(previous.Types.Any(type => type.Name == "meta.implants.CyberEye"), Is.False);
        Assert.That(previous.Types.Any(type => type.Name == "meta.implants.MedicalProgrammer"), Is.True);
        var compilation = new MetaCompiler(previous).Compile(new([new("old.meta", "module old.main;fn main()->void{}")], "old.main"));
        Assert.That(compilation.Success, Is.True);
        Assert.DoesNotThrow(() => MetaVerifier.Verify(compilation.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compilation.Module!)));
    }
}
