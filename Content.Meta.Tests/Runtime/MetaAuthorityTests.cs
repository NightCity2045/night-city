// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaAuthorityTests
{
    private static MetaModule Compile(string source)
    {
        var result = new MetaCompiler().Compile(new([new("test.meta", "module test.authority; " + source)], "test.authority"));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(diagnostic => diagnostic.Code)));
        return result.Module!;
    }
    private static VerifiedArtifact Verify(MetaModule module) => MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module));
    private static MetaValue Stamp(MetaProcess process) => process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"), [MetaValue.Int(1), MetaValue.Int(1), MetaValue.Int(1)]);
    private static MetaValue DoorState(MetaProcess process) => process.CreateRecord(process.GetTypeId("meta.doors.DoorState"), [MetaValue.Bool(false), process.CreateEnum(process.GetTypeId("meta.doors.DoorPhase"), 0), Stamp(process)]);

    [Test]
    public void NativeTypeShapesAndImportSignaturesCannotBeBlessedByGuestMetadata()
    {
        var module = Compile("import meta.doors; link door:Door=\"door\"; fn main()->void { let state=door.snapshot(); }");
        var nativeId = Array.FindIndex(module.Types, type => type.Name == "meta.doors.DoorState");
        var forgedFields = module.Types[nativeId].Fields.ToArray(); forgedFields[0] = forgedFields[0] with { TypeId = 3 };
        var types = module.Types.ToArray(); types[nativeId] = types[nativeId] with { Fields = forgedFields };
        Assert.Throws<MetaFormatException>(() => Verify(module with { Types = types }));
        types = module.Types.ToArray(); types[nativeId] = types[nativeId] with { ContractHash = new string('f', 64) };
        Assert.Throws<MetaFormatException>(() => Verify(module with { Types = types }));
        var imports = module.Imports.ToArray(); imports[0] = imports[0] with { Effect = "Write" };
        Assert.Throws<MetaFormatException>(() => Verify(module with { Imports = imports }));
    }

    [Test]
    public void LatestEventReplacementAndActivationReleaseEachTrustedTokenExactlyOnce()
    {
        var module = Compile("import meta.doors; link door:Door=\"door\"; watch door.stateChanged(state) { yield; }");
        var process = new MetaProcess(Verify(module)); process.TryBindLink(0, 1); process.TryStartMain();
        var authorities = new List<ulong>(); var contexts = new List<ulong>();
        process.EventAuthorityReleased += authorities.Add; process.EventContextReleased += contexts.Add;
        var successfulWatches = 0; process.ActivationCompleted += watch => { if (watch) successfulWatches++; };
        var payload = DoorState(process);
        Assert.That(process.TryEnqueueEvent(0, [payload], 1, 101, 201), Is.True);
        Assert.That(process.TryEnqueueEvent(0, [payload], 2, 102, 202), Is.True);
        Assert.That(authorities, Is.EqualTo(new ulong[] { 101 }));
        Assert.That(contexts, Is.EqualTo(new ulong[] { 201 }));
        Assert.That(process.TryStartNextEvent(), Is.True);
        Assert.That(process.CurrentEventAuthorityToken, Is.EqualTo(102));
        Assert.That(process.CurrentEventContextToken, Is.EqualTo(202));
        process.RunSlice(100, 0);
        Assert.That(process.CurrentEventAuthorityToken, Is.EqualTo(102), "yield preserves the physical actor for this activation");
        process.RunSlice(100, 1);
        Assert.That(process.CurrentEventAuthorityToken, Is.Zero);
        Assert.That(process.CurrentEventContextToken, Is.Zero);
        Assert.That(authorities, Is.EqualTo(new ulong[] { 101, 102 }));
        Assert.That(contexts, Is.EqualTo(new ulong[] { 201, 202 }));
        Assert.That(successfulWatches, Is.EqualTo(1));
        Assert.That(process.TryEnqueueEvent(0, [payload], 3), Is.True); process.TryStartNextEvent(); process.RunSlice(100, 2); process.RunSlice(100, 3);
        Assert.That(successfulWatches, Is.EqualTo(2), "token zero events also refresh watch-driven views");
        process.Stop(MetaStopReason.Stop);
        Assert.That(authorities, Has.Count.EqualTo(2));
    }

    [Test]
    public void OrderedOverflowReleasesAcceptedTokensAndLeavesRejectedTokenWithCaller()
    {
        var module = Compile("import meta.doors; link panel:DoorPanel=\"panel\"; watch panel.requested(request) { yield; }");
        var process = new MetaProcess(Verify(module)); process.TryBindLink(0, 1); process.TryStartMain();
        var released = new List<ulong>(); process.EventAuthorityReleased += released.Add;
        var request = process.CreateRecord(process.GetTypeId("meta.doors.DoorRequest"), [MetaValue.Bool(true), process.CreateString("request"), Stamp(process)]);
        for (var index = 1; index <= 16; index++) Assert.That(process.TryEnqueueEvent(0, [request], index, (ulong) index), Is.True);
        Assert.That(process.TryEnqueueEvent(0, [request], 17, 17), Is.False);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.EventOverflow));
        Assert.That(released, Is.EquivalentTo(Enumerable.Range(1, 16).Select(index => (ulong) index)));
    }

    private sealed class PendingHost : IMetaHost
    {
        public int Calls;
        public int Cancellations;
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException();
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) { Calls++; return MetaHostResult.Awaiting(42); }
        public void Log(MetaProcess process, string text) { }
        public void Cancel(MetaProcess process, long operationId) { Assert.That(operationId, Is.EqualTo(42)); Cancellations++; }
    }

    [Test]
    public void OpaqueReferencesAreProcessLocalRevocableAndReuseBoundedCapacity()
    {
        var artifact = Verify(Compile("fn main()->void {}"));
        var source = new MetaProcess(artifact); var other = new MetaProcess(artifact);
        var type = source.GetTypeId("meta.netrun.NetNodeRef");
        var reference = source.CreateOpaque(type, 123);
        Assert.That(other.TryGetOpaqueToken(reference, out _), Is.False);
        Assert.Catch(() => other.CopyFrom(source, reference));
        Assert.That(source.TryReleaseOpaque(reference), Is.True);
        Assert.That(source.TryReleaseOpaque(reference), Is.False);
        var retained = source.MemoryBytes;
        for (var index = 0; index < 1000; index++)
        {
            var current = source.CreateOpaque(type, (ulong) index);
            Assert.That(source.TryGetOpaqueToken(reference, out _), Is.False);
            Assert.That(source.TryReleaseOpaque(current), Is.True);
        }
        Assert.That(source.MemoryBytes, Is.EqualTo(retained));
    }

    [Test]
    public void HostSuspensionCompletesOnlyExactOperationAndStopCancelsPendingWork()
    {
        var module = Compile("import meta.doors; link door:Door=\"door\"; async fn main()->void { let state=await door.setOpen(true); }");
        var host = new PendingHost(); var process = new MetaProcess(Verify(module), host);
        process.TryBindLink(0, 100); process.TryStartMain(); process.RunSlice(100, 0);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Waiting));
        Assert.That(host.Calls, Is.EqualTo(1));
        Assert.That(process.RunSlice(100, 1).Instructions, Is.Zero);
        var resultType = module.Imports.Single(import => import.Name.EndsWith("setOpen", StringComparison.Ordinal)).ReturnType!.Value;
        var result = process.CreateResult(resultType, DoorState(process));
        Assert.That(process.TryCompleteHost(41, result), Is.False);
        Assert.That(process.TryCompleteHost(42, result), Is.True);
        Assert.That(process.TryCompleteHost(42, result), Is.False);
        process.RunSlice(100, 2);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.Completed));
        Assert.That(host.Cancellations, Is.Zero);
        var another = new MetaProcess(Verify(module), host); another.TryBindLink(0, 100); another.TryStartMain(); another.RunSlice(100, 3); another.Stop(MetaStopReason.PermissionRevoked);
        Assert.That(host.Cancellations, Is.EqualTo(1));
    }

    [Test]
    public void ReclaimedObjectSlotsNeverMakeHeapHandlesValidInAnotherProcess()
    {
        var artifact = Content.Meta.Tests.Bytecode.MetaBytecodeTests.Verify(
            Content.Meta.Tests.Bytecode.MetaBytecodeTests.Module([new(0, MetaOpcode.Safepoint), new(0, MetaOpcode.Br, 0)]));
        var limits = new MetaProcessLimits { MemoryBytes = 4096, ActivationGas = 1_000_000 };
        var second = new MetaProcess(artifact, limits: limits);
        Assert.That(second.TryStart(0, []), Is.True);
        MetaValue other = default;
        for (var index = 0; index < 6; index++) other = second.CreateString("other");
        for (var round = 0; round < 16; round++)
        {
            var first = new MetaProcess(artifact, limits: limits);
            Assert.That(first.TryStart(0, []), Is.True);
            MetaValue old = default;
            for (var index = 0; index < 6; index++) old = first.CreateString(new string('a', 100));
            first.RunSlice(1, round);
            Assert.That(first.State, Is.EqualTo(MetaProcessState.Collecting));
            for (var step = 0; step < 1000 && first.State == MetaProcessState.Collecting; step++) first.RunSlice(32, round);
            Assert.That(first.State, Is.EqualTo(MetaProcessState.Runnable));
            Assert.Catch(() => first.GetString(old));
            var fresh = first.CreateString("fresh");
            Assert.Catch(() => second.GetString(fresh));
            Assert.Catch(() => first.GetString(other));
            Assert.That(second.GetString(other), Is.EqualTo("other"));
        }
    }

    [Test]
    public void SameSessionChecksOnlyLiveLocalOpaqueIdentity()
    {
        var module = Compile("import meta.app; fn equal(left:SessionRef,right:SessionRef)->bool { return app.sameSession(left,right); }");
        var artifact = Verify(module); var process = new MetaProcess(artifact);
        var type = process.GetTypeId("meta.app.SessionRef");
        var first = process.CreateOpaque(type, 7); var second = process.CreateOpaque(type, 7);
        var function = Array.FindIndex(module.Functions, value => value.Name.EndsWith(".equal", StringComparison.Ordinal));
        Assert.That(process.TryStart(function, [first, first]), Is.True); process.RunSlice(100, 0);
        Assert.That(process.LastResult?.AsBool(), Is.True);
        Assert.That(process.TryStart(function, [first, second]), Is.True); process.RunSlice(100, 1);
        Assert.That(process.LastResult?.AsBool(), Is.False, "equal native token payloads do not merge distinct session identities");
        Assert.That(process.TryStart(function, [first, first]), Is.True); process.TryReleaseOpaque(first); process.RunSlice(100, 2);
        Assert.That(process.LastResult?.AsBool(), Is.False, "revocation between activations and execution must be observed");
    }

    [Test]
    public void CrossArtifactContainerNameCannotReplaceDifferentElementSchema()
    {
        MetaModule Typed(int item) => Content.Meta.Tests.Bytecode.MetaBytecodeTests.Module([new(0, MetaOpcode.Return)]) with
        { Types = [.. MetaTypeDef.Primitives, new() { Id = 6, Kind = MetaTypeKind.List, Name = "test.sameName", Args = [item] }] };
        var source = new MetaProcess(Content.Meta.Tests.Bytecode.MetaBytecodeTests.Verify(Typed(3)));
        var target = new MetaProcess(Content.Meta.Tests.Bytecode.MetaBytecodeTests.Verify(Typed(5)));
        var list = source.CreateList(6, [MetaValue.Int(1)]);
        Assert.Catch(() => target.CopyFrom(source, list));
    }
}
