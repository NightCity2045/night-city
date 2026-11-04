// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaMedicalProgrammerTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void MedicalProgrammingUsesVerifiedNativeCommand(int mode)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "Resources", "Meta", "Medical"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "Resources", "Meta", "Medical", "Programmer.meta"))
            .Replace("board.flash(0)", $"board.flash({mode})", StringComparison.Ordinal);
        var compiled = new MetaCompiler().Compile(new([new("Programmer.meta", source)], "medical.programmer"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        Assert.That(compiled.Module!.Manifest.RequiredScopes, Is.EquivalentTo(new[] { "implant.medical.flash", "implant.medical.read" }));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module));
        var fixture = MetaTestHost.ParseFixture($$"""
            {"rootModule":"medical.programmer","responses":[{"member":"meta.implants.MedicalProgrammer.flash","pending":true,"value":true},{"member":"meta.implants.MedicalProgrammer.selfTest","value":true}],
            "expectedCommands":["flash({{mode}})","selfTest()"],"expectedLogs":[],"events":[]}
            """);
        var result = MetaTestHost.Run(artifact, fixture);
        Assert.That(result.Passed, Is.True, result.FailureCode + ":" + result.Fault);
    }
    [Test]
    public void PreviousCatalogStillVerifiesExistingPrograms()
    {
        var catalog = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreMedicalCatalogHash);
        Assert.That(catalog.Types.Any(t => t.Name == "meta.implants.MedicalProgrammer"), Is.False);
        var compiled = new MetaCompiler(catalog).Compile(new([new("old.meta", "module test.old;fn main()->void{}")], "test.old"));
        Assert.That(compiled.Success, Is.True);
        Assert.DoesNotThrow(() => MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)));
    }
}
