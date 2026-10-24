// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaCatalogCompatibilityTests
{
    [Test]
    public void NativeControlsCatalogShippedBeforeFactoriesStillLinksAfterTheExtension()
    {
        var catalog = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.NativeControlsCatalogHash);
        var legacy = new MetaCompiler(catalog).Compile(new([new("old.meta", "module test.controls;import meta.system;fn main()->void{system.log(\"controls\");}")], "test.controls"));
        Assert.That(legacy.Success, Is.True, string.Join(',', legacy.Diagnostics.Select(d => d.Code)));
        var verified = MetaVerifier.Verify(legacy.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(legacy.Module!));
        Assert.That(MetaTestHost.Run(verified, new("test.controls", [], [], ["controls"], [])).Passed, Is.True);
        Assert.That(catalog.Types.Any(type => type.Name == "meta.industrial.Fabricator"), Is.False);
    }
    private static MetaCompilationResult CompileBaseline(string source, string module, bool library = false)
    {
        var catalog = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.BaselineCatalogHash);
        var result = new MetaCompiler(catalog).Compile(new([new("legacy.meta", source)], module, "maker.legacy", Library: library));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        return result;
    }

    [Test]
    public void PreviouslyPublishedCatalogArtifactStillVerifiesAndRunsAfterAdditiveNativeApis()
    {
        var old = CompileBaseline("module test.legacy;import meta.system;fn main()->void{system.log(\"legacy\");}", "test.legacy");
        Assert.That(old.Module!.Types.Any(t => t.Name == "meta.vehicle.DriverAssistance"), Is.False);
        var verified = MetaVerifier.Verify(old.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(old.Module));
        var run = MetaTestHost.Run(verified, new("test.legacy", [], [], ["legacy"], []));
        Assert.That(run.Passed, Is.True, run.FailureCode);
        Assert.That(verified.Manifest.ApiCatalogHash, Is.EqualTo(MetaApiCatalog.BaselineCatalogHash));
    }

    [Test]
    public void ClosedLibraryFromPreviousCatalogLinksUsingCurrentBuiltinGroupingHashes()
    {
        var old = CompileBaseline("module maker.legacy;import meta.vehicle;pub fn normal()->vehicle.EngineMode{return vehicle.EngineMode.Normal;}", "maker.legacy", true);
        var dependency = new MetaBinaryLibrary("maker.legacy", "1.0.0", old.ArtifactHash!, old.Artifact!, old.Exports!);
        var current = new MetaCompiler().Compile(new([new("main.meta", """
            module test.current; import maker.legacy; import meta.vehicle; import meta.system;
            fn main()->void {if(legacy.normal()==vehicle.EngineMode.Normal){system.log("compatible");}}
            """)], "test.current", BinaryDependencies: [dependency]));
        Assert.That(current.Success, Is.True, string.Join('\n', current.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        var verified = MetaVerifier.Verify(current.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(current.Module!));
        var run = MetaTestHost.Run(verified, new("test.current", [], [], ["compatible"], []));
        Assert.That(run.Passed, Is.True, run.FailureCode);
        Assert.That(current.Module!.Imports.Single(i => i.Kind == "BuiltinModule" && i.Name == "meta.vehicle").ContractHash,
            Is.EqualTo(MetaApiCatalog.Default.ModuleHash("meta.vehicle")));
    }

    [Test]
    public void HistoricalHashNeverGrantsTrustToChangedNativeDtoOrUnknownCatalog()
    {
        var old = CompileBaseline("module test.legacy;fn main()->void{}", "test.legacy");
        var module = old.Module!;
        var type = module.Types.Single(t => t.Name == "meta.system.SampleStamp");
        var forged = module with { Types = module.Types.Select(t => t.Id == type.Id ? t with { Fields = [.. t.Fields.Select((f, i) => i == 0 ? f with { Name = "forged" } : f)] } : t).ToArray() };
        Assert.Throws<MetaFormatException>(() => MetaVerifier.Verify(MetaBytecodeWriter.Write(forged), MetaApiCatalog.Default.CreateVerificationCatalog(forged)));
        var unknown = module with { Manifest = module.Manifest with { ApiCatalogHash = new string('f', 64) } };
        Assert.Throws<MetaFormatException>(() => MetaApiCatalog.Default.CreateVerificationCatalog(unknown));
    }
}
