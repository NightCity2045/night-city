// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Mui;

[TestFixture]
public sealed class MuiStreamingCodecTests
{
    private static readonly IReadOnlyDictionary<string, MuiField> Fields = new Dictionary<string, MuiField>
    {
        ["name"] = new(MuiValueKind.String, MaxLength: 32), ["enabled"] = new(MuiValueKind.Boolean),
        ["rows"] = new(MuiValueKind.Array, MaxLength: 64), ["optional"] = new(MuiValueKind.Integer, Required: false),
        ["values"] = new(MuiValueKind.Object),
    };
    private const string Json = "{\"name\":\"Я🙂\",\"enabled\":true,\"rows\":[1,2,3],\"optional\":null,\"values\":{\"one\":1}}";

    private static VerifiedArtifact Compile(string source)
    {
        var compiled = new MetaCompiler().Compile(new([new("main.meta", "module codec.test; " + source)], "codec.test"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(value => value.Code)));
        return MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
    }
    private static VerifiedArtifact StateArtifact() => Compile("struct State{name:string;enabled:bool;rows:array<int>;optional:option<int>;values:map<string,int>;} fn noop()->int{return 1;}");
    private static int Type(VerifiedArtifact artifact, string name = "codec.test.State") => Enumerable.Range(0, artifact.TypeCount).Single(id => artifact.GetType(id).Name == name);
    private static MuiProjectionResult Project(MetaProcess process, VerifiedArtifact artifact, int type, MetaValue value, IReadOnlyDictionary<string, MuiField> fields)
    {
        using var writer = new MuiProjectionWriter(process, artifact, type, value, fields); var steps = 0;
        while (writer.Progress == MuiCodecProgress.Working && steps++ < 10000)
        {
            Assert.That(writer.NextGas, Is.InRange(1, 8192));
            Assert.That(writer.NextReservationBytes, Is.LessThan(2 * 1024 * 1024));
            writer.Step();
        }
        Assert.That(steps, Is.GreaterThan(5)); Assert.That(writer.Progress, Is.EqualTo(MuiCodecProgress.Complete));
        return writer.Result!;
    }

    [Test]
    public void IncrementalInputAndProjectionMatchExistingTypedCodecAndExposeExactFieldJson()
    {
        var artifact = StateArtifact(); var type = Type(artifact); var process = new MetaProcess(artifact);
        Assert.That(MuiInputReader.TryCreate(process, artifact, type, Json, Fields, out var reader), Is.True);
        using (reader)
        {
            var steps = 0;
            while (reader.Progress == MuiCodecProgress.Working && steps++ < 10000)
            { Assert.That(reader.NextGas, Is.InRange(1, 8192)); reader.Step(); }
            Assert.That(reader.Progress, Is.EqualTo(MuiCodecProgress.Complete)); Assert.That(steps, Is.GreaterThan(10));
            var result = Project(process, artifact, type, reader.Result, Fields);
            Assert.That(MuiDtoCodec.TryProject(process, artifact, type, reader.Result, Fields, out var old), Is.True);
            Assert.That(result.Json, Is.EqualTo(old)); Assert.That(MuiDataValidator.TryValidate(result.Json, Fields, MuiLimits.InitialStateBytes, out _), Is.True);
            using var document = JsonDocument.Parse(result.Json);
            foreach (var field in result.Fields)
                Assert.That(field.Value, Is.EqualTo(document.RootElement.GetProperty(field.Key).GetRawText()));
        }
    }

    [TestCase(2, true)] [TestCase(6, false)]
    public void IncrementalUnicodeOutputHonorsTheEncodedByteLimit(int count, bool accepted)
    {
        var artifact = Compile("struct State{rows:array<string>;} fn noop()->void{}"); var type = Type(artifact);
        var process = new MetaProcess(artifact); var array = artifact.GetType(type).Fields[0].TypeId;
        var text = process.CreateString(string.Concat(Enumerable.Repeat("Я🙂e\u0301", 512)));
        var value = process.CreateRecord(type, [process.CreateArray(array, Enumerable.Repeat(text, count).ToArray())]);
        using var writer = new MuiProjectionWriter(process, artifact, type, value, new Dictionary<string, MuiField> { ["rows"] = new(MuiValueKind.Array, MaxLength: 512) });
        for (var step = 0; step < 10000 && writer.Progress == MuiCodecProgress.Working; step++) writer.Step();
        Assert.That(writer.Progress, Is.EqualTo(accepted ? MuiCodecProgress.Complete : MuiCodecProgress.Invalid));
    }

    [Test]
    public void TypedDefaultVariantsCountTowardConstructionWorkBeforeAnyAllocation()
    {
        var optional = string.Join(' ', Enumerable.Range(0, 64).Select(index => "f" + index + ":option<int>;"));
        var artifact = Compile("struct Row{" + optional + "} struct State{rows:array<Row>;} fn noop()->void{}");
        var type = Type(artifact); var process = new MetaProcess(artifact); var before = process.MemoryBytes;
        var json = "{\"rows\":[" + string.Join(',', Enumerable.Repeat("{}", 64)) + "]}";
        var schema = new Dictionary<string, MuiField> { ["rows"] = new(MuiValueKind.Array, MaxLength: 64) };
        Assert.That(MuiDtoCodec.CanDecodeInput(artifact, type, json, schema), Is.False);
        Assert.That(MuiInputReader.TryCreate(process, artifact, type, json, schema, out _), Is.False);
        Assert.That(process.MemoryBytes, Is.EqualTo(before)); Assert.That(process.Fault, Is.Null);
    }

    [Test]
    public void SmallFormsReserveOnlyTheirActualRootSlotsAndDisposeOnce()
    {
        var artifact = Compile("struct State{} fn noop()->void{}"); var process = new MetaProcess(artifact, limits: new() { MemoryBytes = 4096 });
        var before = process.MemoryBytes;
        Assert.That(MuiInputReader.TryCreate(process, artifact, Type(artifact), "{}", new Dictionary<string, MuiField>(), out var reader), Is.True);
        Assert.That(process.MemoryBytes - before, Is.LessThan(512));
        while (reader.Progress == MuiCodecProgress.Working) reader.Step();
        Assert.That(reader.Progress, Is.EqualTo(MuiCodecProgress.Complete));
        reader.Dispose(); var disposed = process.MemoryBytes; reader.Dispose();
        Assert.That(process.MemoryBytes, Is.EqualTo(disposed));
        Assert.That(process.TryCreateHostRoots(4096, out _), Is.False);
    }

    [Test]
    public void PinnedIntermediateInputSurvivesGuestCollectionBetweenCodecSteps()
    {
        var artifact = StateArtifact(); var process = new MetaProcess(artifact, limits: new() { MemoryBytes = 32768 });
        Assert.That(MuiInputReader.TryCreate(process, artifact, Type(artifact), Json, Fields, out var reader), Is.True);
        using (reader)
        {
            for (var step = 0; step < 3; step++) reader.Step();
            for (var index = 0; index < 8; index++) process.CreateString(new string('g', 1000));
            Assert.That(process.TryStart(0, []), Is.True);
            var collected = false;
            for (var work = 0; work < 10000 && process.State != MetaProcessState.Idle; work++)
            { process.RunSlice(1, 0); collected |= process.State == MetaProcessState.Collecting; }
            Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
            Assert.That(collected, Is.True, "The intermediate roots must survive actual incremental collection.");
            while (reader.Progress == MuiCodecProgress.Working) reader.Step();
            Assert.That(reader.Progress, Is.EqualTo(MuiCodecProgress.Complete));
            var projection = Project(process, artifact, Type(artifact), reader.Result, Fields);
            using var json = JsonDocument.Parse(projection.Json);
            Assert.That(json.RootElement.GetProperty("name").GetString(), Is.EqualTo("Я🙂"));
        }
    }

    [Test]
    public void RootLeaseCannotPinForeignValuesAndMayBeDisposedDuringIncrementalGc()
    {
        var artifact = Compile("fn noop()->int{return 1;}"); var process = new MetaProcess(artifact, limits: new() { MemoryBytes = 32768 });
        var foreign = new MetaProcess(artifact);
        Assert.That(process.TryCreateHostRoots(2, out var roots), Is.True);
        Assert.That(roots.TryAdd(foreign.CreateString("foreign")), Is.False);
        Assert.That(roots.TryAdd(process.CreateString("owned")), Is.True);
        for (var index = 0; index < 9; index++) process.CreateString(new string('g', 1000));
        Assert.That(process.TryStart(0, []), Is.True); process.RunSlice(1, 0);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Collecting));
        roots.Dispose();
        for (var work = 0; work < 10000 && process.State != MetaProcessState.Idle; work++) process.RunSlice(1, 0);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle)); Assert.That(process.Fault, Is.Null);
        process.Stop(); Assert.That(roots.TryAdd(MetaValue.Int(1)), Is.False);
    }

    [Test]
    public void HostArgumentAllocationFailureKeepsItsHeapLimitFaultCode()
    {
        var artifact = Compile("fn identity(value:string)->string{return value;}");
        var process = new MetaProcess(artifact, limits: new() { MemoryBytes = 4096 });
        Assert.That(process.TryStartHostInvocation(0, owner => [owner.CreateString(new string('x', 5000))]), Is.False);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Faulted));
        Assert.That(process.Fault!.Code, Is.EqualTo(MetaVmFaultCode.HeapLimit));
    }

    [Test]
    public void IncrementalPatchAppliesThroughTheExistingRevisionProtocol()
    {
        var artifact = StateArtifact(); var type = Type(artifact); var process = new MetaProcess(artifact);
        var initial = MuiDtoCodec.DecodeInput(process, artifact, type, Json, Fields);
        var previous = Project(process, artifact, type, initial, Fields);
        var changed = MuiDtoCodec.DecodeInput(process, artifact, type, Json.Replace("Я🙂", "Bob", StringComparison.Ordinal), Fields);
        var next = Project(process, artifact, type, changed, Fields);
        using var patch = new MuiPatchWriter(previous.Fields, next, false);
        for (var step = 0; step < 100 && patch.Progress == MuiCodecProgress.Working; step++) patch.Step();
        Assert.That(patch.Progress, Is.EqualTo(MuiCodecProgress.Complete)); Assert.That(patch.FullSnapshot, Is.False);
        using var session = new MuiSessionState(Guid.NewGuid(), Fields);
        Assert.That(session.TryApplySnapshot(0, previous.Json), Is.EqualTo(MuiUpdateResult.Applied));
        Assert.That(session.TryApplyPatch(0, 1, patch.Body!), Is.EqualTo(MuiUpdateResult.Applied));
        Assert.That(session.Read("name")!.ToString(), Is.EqualTo("Bob"));
    }

    [Test]
    public void PatchLargerThanItsWireBudgetUsesTheValidatedFullSnapshot()
    {
        var artifact = Compile("struct State{rows:array<string>;} fn noop()->void{}"); var type = Type(artifact);
        var process = new MetaProcess(artifact); var array = artifact.GetType(type).Fields[0].TypeId;
        var schema = new Dictionary<string, MuiField> { ["rows"] = new(MuiValueKind.Array, MaxLength: 64) };
        var previous = Project(process, artifact, type, process.CreateRecord(type, [process.CreateArray(array, [])]), schema);
        var text = process.CreateString(new string('x', 4096));
        var next = Project(process, artifact, type, process.CreateRecord(type, [process.CreateArray(array, Enumerable.Repeat(text, 5).ToArray())]), schema);
        using var patch = new MuiPatchWriter(previous.Fields, next, false);
        for (var step = 0; step < 100 && patch.Progress == MuiCodecProgress.Working; step++) patch.Step();
        Assert.That(patch.Progress, Is.EqualTo(MuiCodecProgress.Complete)); Assert.That(patch.FullSnapshot, Is.True);
        Assert.That(patch.Body, Is.EqualTo(next.Json));
    }
}
