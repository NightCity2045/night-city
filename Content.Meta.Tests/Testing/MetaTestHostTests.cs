// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Compilation;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaTestHostTests
{
    [TestCase("{\"rootModule\":\"test.program\",\"responses\":null,\"events\":[]}")]
    [TestCase("{\"rootModule\":\"test.program\",\"responses\":[],\"events\":null}")]
    [TestCase("{\"rootModule\":\"test.program\",\"responses\":[null],\"events\":[]}")]
    [TestCase("{\"rootModule\":\"test.program\",\"responses\":[],\"events\":[null]}")]
    [TestCase("{\"rootModule\":\"test.program\",\"responses\":[{\"member\":\"meta.files.FileStore.readText\",\"errorCode\":\"IoFailure\",\"errorMessage\":null}],\"events\":[]}")]
    public void NullFixtureStructuresAreRejectedBeforeExecution(string json) => Assert.Throws<ArgumentException>(() => MetaTestHost.ParseFixture(json));

    [Test]
    public void DuplicatePropertiesAndNullSuitesCannotBypassFixtureValidation()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => MetaTestHost.ParseFixture("{\"rootModule\":\"test.program\",\"rootModule\":\"other\",\"responses\":[],\"events\":[]}"));
        Assert.Throws<System.Text.Json.JsonException>(() => MetaTestHost.ParseFixture("{\"rootModule\":\"test.program\",\"responses\":[{\"member\":\"a\",\"value\":{\"tag\":\"none\",\"tag\":\"some\"}}],\"events\":[]}"));
        Assert.Throws<ArgumentException>(() => MetaTestHost.ParseSuite("{\"version\":\"1.0\",\"programs\":null}"));
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.program;fn main()->void{}")], "test.program"));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var invalid = MetaTestHost.Run(artifact, new("test.program", null!, []));
        Assert.That(invalid.Status, Is.EqualTo(MetaTestStatus.InvalidFixture));
        var unknownError = MetaTestHost.ParseFixture("{\"rootModule\":\"test.program\",\"responses\":[{\"member\":\"meta.files.FileStore.readText\",\"errorCode\":\"InventedError\"}],\"events\":[]}");
        Assert.That(MetaTestHost.Run(artifact, unknownError).Status, Is.EqualTo(MetaTestStatus.InvalidFixture));
    }

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Examples"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }
    public static IEnumerable<string> Examples() => MetaTestHost.ParseSuite(File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Examples", "fixtures.json"))).Programs.Select(p => p.RootModule);

    [TestCaseSource(nameof(Examples))]
    public async Task AllFifteenProgramsRunThroughWorkerVerifierAndBoundedHostFixture(string module)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The worker currently requires Windows Job Objects.");
        var suite = MetaTestHost.ParseSuite(File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Examples", "fixtures.json")));
        var fixture = suite.Programs.Single(p => p.RootModule == module);
        var source = File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Examples", module + ".meta"));
        await using var pool = new MetaCompilerWorkerPool(MetaTestWorkerBundle.Resolve());
        var compiled = await pool.CompileAsync("fixture-test", new([new("fixture.meta", source)], module));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var result = MetaTestHost.Run(verified, fixture);
        Assert.That(result.Status, Is.EqualTo(MetaTestStatus.Passed), result.FailureCode + ":" + result.Fault + " Logs=" + string.Join('|', result.Logs) + " Commands=" + string.Join('|', result.Commands.Select(c => c.Trace)));
        Assert.That(result.GasUsed, Is.GreaterThan(0));
    }

    [Test]
    public void ExpectedOutputMismatchAndRunawayHaveExplicitResults()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.program;import meta.system;fn main()->void{system.log(\"actual\");}")], "test.program"));
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var result = MetaTestHost.Run(verified, new("test.program", [], [], ["different"], []));
        Assert.That(result.Status, Is.EqualTo(MetaTestStatus.Failed)); Assert.That(result.FailureCode, Is.EqualTo("meta-test-log-mismatch"));
        var runaway = new MetaCompiler().Compile(new([new("test.meta", "module test.program;fn main()->void{while(true){yield;}}")], "test.program"));
        var runawayArtifact = MetaVerifier.Verify(runaway.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(runaway.Module!));
        var bounded = MetaTestHost.Run(runawayArtifact, new("test.program", [], []), new(MaxGas: 1000));
        Assert.That(bounded.Status, Is.EqualTo(MetaTestStatus.LimitExceeded).Or.EqualTo(MetaTestStatus.Faulted));
        Assert.That(bounded.Slices, Is.LessThan(4097));
    }

    [Test]
    public void FixtureJsonAndNativeDtoShapesAreStrictAndBounded()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => MetaTestHost.ParseFixture("{\"rootModule\":\"test.program\",\"responses\":[],\"events\":[],\"hostPath\":\"C:/secret\"}"));
        Assert.Throws<ArgumentException>(() => MetaTestHost.ParseFixture(new string(' ', 65537)));
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.program;import meta.sensors;link sensor:TemperatureSensor=\"sensor\";watch sensor.temperatureChanged(sample){}")], "test.program"));
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture("""
            {"rootModule":"test.program","responses":[],"events":[{"slot":"sensor","event":"temperatureChanged","sequence":1,"arguments":[{"celsius":20,"stamp":{"epoch":1,"sequence":1,"tick":60},"authority":"admin"}]}]}
            """);
        var result = MetaTestHost.Run(verified, fixture);
        Assert.That(result.Status, Is.EqualTo(MetaTestStatus.InvalidFixture));
    }

    [TestCase("null")]
    [TestCase("{\"tag\":\"invented\"}")]
    [TestCase("{\"tag\":\"some\",\"value\":\"invented\"}")]
    public void MalformedNativeResponseIsAnInvalidFixtureRatherThanAVmFault(string option)
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", """
            module test.program; import meta.industrial;
            link machine:Fabricator="machine";
            async fn main()->void { match(await machine.start("Wirecutter",1,"batch")){ok(receipt)=>{}err(error)=>{}} }
            """)], "test.program"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture("""
            {"rootModule":"test.program","responses":[{"member":"meta.industrial.Fabricator.start","value":{
                "operationId":"batch","status":"Running","requested":1,"completed":0,"errorCode":OPTION,
                "stamp":{"epoch":1,"sequence":1,"tick":60}}}],"events":[]}
            """.Replace("OPTION", option, StringComparison.Ordinal));
        var result = MetaTestHost.Run(artifact, fixture);
        Assert.That(result.Status, Is.EqualTo(MetaTestStatus.InvalidFixture));
        Assert.That(result.FailureCode, Does.StartWith("meta-test-"));
        Assert.That(result.Fault, Is.Null);
    }

    [Test]
    public void AGuestNumericFaultRemainsAVmFault()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", """
            module test.program; import meta.system; import meta.text;
            fn denominator()->int{return 0;}
            fn main()->void{system.log(text.fromInt(1/denominator()));}
            """)], "test.program"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var result = MetaTestHost.Run(artifact, new("test.program", [], []));
        Assert.That(result.Status, Is.EqualTo(MetaTestStatus.Faulted));
        Assert.That(result.FailureCode, Is.EqualTo("meta-test-vm-fault"));
        Assert.That(result.Fault?.Code, Is.EqualTo(Content.Meta.Runtime.MetaVmFaultCode.NumericFault));
    }
}
