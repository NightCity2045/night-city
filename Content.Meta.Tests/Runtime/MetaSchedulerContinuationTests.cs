// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Tests.Bytecode;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaSchedulerContinuationTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    private sealed class Calls(int count) : IMetaSchedulerContinuations
    {
        public int Started;
        public int Cancelled;
        public int Cost = 8;
        public bool Active;
        public int Function;
        public readonly List<MetaValue> Results = [];
        public Func<MetaProcess, int, MetaValue>? Argument;
        public Action<MetaProcess, MetaContinuationKind>? OnExecute;
        public Action? OnComplete;
        public Action? OnCancel;
        public int CompletionChunks = 1;
        private int _chunks;
        public bool TryPeek(string processId, out MetaContinuationStep step)
        {
            step = new(Started * 2L + (Active ? 2 : 1), Active ? MetaContinuationKind.CompleteInvocation : MetaContinuationKind.PrepareInvocation, Cost);
            return Active || Started < count;
        }
        public MetaContinuationResult Execute(string processId, MetaProcess process, in MetaContinuationStep step)
        {
            OnExecute?.Invoke(process, step.Kind);
            if (step.Kind == MetaContinuationKind.PrepareInvocation)
            {
                Assert.That(Active, Is.False);
                Assert.That(process.TryStart(Function, [Argument?.Invoke(process, Started + 1) ?? MetaValue.Int(Started + 1)]), Is.True);
                Started++; Active = true; _chunks = 0;
                return MetaContinuationResult.InvocationStarted;
            }
            Assert.That(Active, Is.True);
            Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
            Assert.That(process.LastResult, Is.Not.Null);
            if (++_chunks < CompletionChunks) return MetaContinuationResult.Progress;
            Results.Add(process.LastResult!.Value); Active = false;
            OnComplete?.Invoke();
            return MetaContinuationResult.InvocationCompleted;
        }
        public void Cancel(string processId, MetaStopReason reason) { Cancelled++; Active = false; OnCancel?.Invoke(); }
    }

    private static MetaProcess Identity(bool yields = false)
    {
        var instructions = yields ? new MetaInstruction[] { new(0, MetaOpcode.Yield), new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.Return) }
            : [new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.Return)];
        var module = MetaBytecodeTests.Module(instructions, result: 3, maximum: 1, locals: [new(3, false, true)]);
        module = module with { Functions = [module.Functions[0] with { ParameterTypes = [3] }] };
        return new(MetaBytecodeTests.Verify(module));
    }

    private static MetaScheduler Scheduler(MetaProcess process, Calls calls, MetaSchedulerLimits? limits = null, Clock? clock = null)
    {
        var scheduler = new MetaScheduler(limits, timeProvider: clock);
        scheduler.SetClock(1, 1, 1000.0 / 60);
        Assert.That(scheduler.TryRegister("app", "owner", "host", process, continuations: calls), Is.True);
        Assert.That(scheduler.NotifyContinuation("app"), Is.True);
        return scheduler;
    }

    [Test]
    public void FiftyDistinctSequentialInvocationsUseOneProcessAndOneTickBudget()
    {
        var process = Identity(); var calls = new Calls(50); var scheduler = Scheduler(process, calls);
        for (var index = 0; index < 100; index++) scheduler.NotifyContinuation("app");
        var tick = scheduler.RunTick(0, 2000);
        Assert.That(calls.Results.Select(result => result.AsInt()), Is.EqualTo(Enumerable.Range(1, 50)));
        Assert.That(tick.Slices, Is.EqualTo(50)); Assert.That(tick.ContinuationSteps, Is.EqualTo(100));
        Assert.That(tick.GuestGasUsed, Is.EqualTo(100)); Assert.That(tick.ContinuationGasUsed, Is.EqualTo(800));
        Assert.That(tick.GasUsed, Is.EqualTo(tick.GuestGasUsed + tick.ContinuationGasUsed));
        Assert.That(tick.GasUsed, Is.EqualTo(900)); Assert.That(scheduler.RegisteredProcesses, Is.EqualTo(1));
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
    }

    [Test]
    public void ContinuationsCannotResetGasOrRunTwiceWithTheSameTickIdentity()
    {
        var process = Identity(); var calls = new Calls(50); var scheduler = Scheduler(process, calls);
        var first = scheduler.RunTick(0, 18);
        Assert.That(calls.Results.Count, Is.EqualTo(1)); Assert.That(first.GasUsed, Is.EqualTo(18));
        Assert.Throws<InvalidOperationException>(() => scheduler.RunTick(0, 10000));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SetClock(1, 0, 16));
        scheduler.SetClock(1, 2, 16);
        Assert.That(scheduler.RunTick(16, 18).GasUsed, Is.EqualTo(18));
        Assert.That(calls.Results.Count, Is.EqualTo(2));
    }

    [Test]
    public void NativePreparationAndCompletionShareTheOriginalWallDeadline()
    {
        var clock = new Clock(); var calls = new Calls(3);
        calls.OnExecute = (_, _) => clock.Milliseconds += 3;
        var scheduler = Scheduler(Identity(), calls, clock: clock);
        var first = scheduler.RunTick(0, 10000, TimeSpan.FromMilliseconds(2));
        Assert.That(first.ContinuationSteps, Is.EqualTo(1)); Assert.That(first.Slices, Is.Zero);
        Assert.That(first.WallExhausted, Is.True); Assert.That(calls.Results, Is.Empty);
        scheduler.SetClock(1, 2, 16);
        var second = scheduler.RunTick(16, 10000, TimeSpan.FromMilliseconds(2));
        Assert.That(second.Slices, Is.EqualTo(1)); Assert.That(second.ContinuationSteps, Is.EqualTo(1));
        Assert.That(calls.Results.Count, Is.EqualTo(1)); Assert.That(calls.Started, Is.EqualTo(1));
    }

    [Test]
    public void PrefixTimeCountsAndLegacyFreePublicationCannotBeMixedWithContinuations()
    {
        var clock = new Clock(); var scheduler = Scheduler(Identity(), new Calls(3), clock: clock);
        var admitted = false;
        var tick = scheduler.RunTick(0, 10000, TimeSpan.FromMilliseconds(2), () => clock.Milliseconds += 2, () => admitted = true);
        Assert.That(tick.GasUsed, Is.Zero); Assert.That(admitted, Is.False); Assert.That(tick.WallExhausted, Is.True);
        scheduler.SetClock(1, 2, 16);
        Assert.Throws<ArgumentException>(() => scheduler.RunTick(16, 1000, publishDeltas: () => { }));
        var legacyPublished = false;
        new MetaScheduler().RunTick(0, 0, TimeSpan.Zero, publishDeltas: () => legacyPublished = true);
        Assert.That(legacyPublished, Is.True);
    }

    [Test]
    public void PerHeaderIngressChecksTheOriginalDeadlineWithoutGrantingAnotherBudget()
    {
        var clock = new Clock(); var scheduler = Scheduler(Identity(), new Calls(3), clock: clock); var promoted = 0;
        Assert.That(scheduler.HasTimeRemaining, Is.False);
        var result = scheduler.RunTick(0, 1000, TimeSpan.FromMilliseconds(2), boundedAdmission: () =>
        {
            while (promoted < 32 && scheduler.HasTimeRemaining) { promoted++; clock.Milliseconds++; }
        });
        Assert.That(promoted, Is.EqualTo(2)); Assert.That(result.GasUsed, Is.Zero);
        Assert.That(result.WallExhausted, Is.True); Assert.That(scheduler.HasTimeRemaining, Is.False);
    }

    [Test]
    public void NativeAndGuestGasSharePhysicalWindowsAcrossPrincipals()
    {
        var scheduler = new MetaScheduler(); scheduler.SetClock(1, 1, 16);
        Assert.That(scheduler.TryConfigureDevice("shared", new(2, 8 * 1024 * 1024, 36, 262144)), Is.True);
        var first = new Calls(10); var second = new Calls(10);
        scheduler.TryRegister("one", "alice", "shared", Identity(), continuations: first);
        scheduler.TryRegister("two", "bob", "shared", Identity(), continuations: second);
        scheduler.NotifyContinuation("one"); scheduler.NotifyContinuation("two");
        Assert.That(scheduler.RunTick(0, 10000).GasUsed, Is.EqualTo(36));
        Assert.That(first.Results.Count + second.Results.Count, Is.EqualTo(2));
        scheduler.SetClock(1, 2, 16); Assert.That(scheduler.RunTick(999, 10000).GasUsed, Is.Zero);
        scheduler.SetClock(1, 3, 16); Assert.That(scheduler.RunTick(1000, 10000).GasUsed, Is.EqualTo(36));
        Assert.That(scheduler.TryGetDeviceUsage("shared", out var usage), Is.True);
        Assert.That(usage.TotalGas, Is.EqualTo(72));
    }

    [Test]
    public void MoreSessionsDoNotCreateHdrRoundRobinCreditOrStarveAnotherVm()
    {
        var process = Identity(); var calls = new Calls(50); var scheduler = Scheduler(process, calls);
        var other = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Br, 0)])));
        other.TryStart(0, []); scheduler.TryRegister("other", "other-owner", "other-host", other);
        calls.OnComplete = () => Assert.That(other.ActivationGas, Is.GreaterThanOrEqualTo(256));
        scheduler.RunTick(0, 16384);
        Assert.That(calls.Results.Count, Is.InRange(1, 49), "Competing guest work consumes the same tick gas.");
        for (var tick = 2; tick <= 3; tick++) { scheduler.SetClock(1, tick, 16); scheduler.RunTick(tick * 16, 16384); }
        Assert.That(calls.Results.Count, Is.EqualTo(50)); Assert.That(other.ActivationGas, Is.GreaterThan(0));
        Assert.That(scheduler.RegisteredProcesses, Is.EqualTo(2));
    }

    [TestCase(false)] [TestCase(true)]
    public void StopOrFreshProofLossInACompletionPreventsTheNextActivation(bool proofLoss)
    {
        var calls = new Calls(50); var process = Identity(); var scheduler = Scheduler(process, calls); var allowed = true;
        Assert.That(scheduler.TryReserveAuxiliaryMemory("app", 1024, out _), Is.True);
        calls.OnComplete = () =>
        {
            if (calls.Results.Count != 7) return;
            if (proofLoss) allowed = false; else scheduler.QueueStop("app", MetaStopReason.PermissionRevoked);
        };
        scheduler.RunTick(0, 20000, beforeSlice: _ => allowed);
        Assert.That(calls.Started, Is.EqualTo(7)); Assert.That(calls.Results.Count, Is.EqualTo(7));
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
        Assert.That(calls.Cancelled, Is.EqualTo(1)); Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.Zero);
        scheduler.Remove("app"); Assert.That(calls.Cancelled, Is.EqualTo(1));
    }

    [Test]
    public void YieldDefersUntilNextExplicitTickWithoutResettingActivationGas()
    {
        var calls = new Calls(2); var process = Identity(true); var scheduler = Scheduler(process, calls);
        var first = scheduler.RunTick(0, 10000);
        Assert.That(first.Slices, Is.EqualTo(1)); Assert.That(calls.Results, Is.Empty);
        Assert.That(process.ActivationGas, Is.EqualTo(1)); Assert.That(calls.Started, Is.EqualTo(1));
        scheduler.SetClock(1, 2, 16); scheduler.RunTick(16, 10000);
        Assert.That(calls.Results.Count, Is.EqualTo(1)); Assert.That(calls.Started, Is.EqualTo(2));
        scheduler.SetClock(1, 3, 16); scheduler.RunTick(32, 10000);
        Assert.That(calls.Results.Select(result => result.AsInt()), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(process.ActivationGas, Is.EqualTo(3));
    }

    [Test]
    public void AuxiliaryMemoryIsTransactionalAndSharedAcrossPhysicalOwners()
    {
        var one = Identity(); var reservation = one.MemoryReservationBytes; var scheduler = new MetaScheduler();
        scheduler.TryConfigureDevice("shared", new(2, 2 * reservation + 100, 1000, 262144));
        scheduler.TryRegister("one", "alice", "shared", one);
        scheduler.TryRegister("two", "bob", "shared", Identity());
        Assert.That(scheduler.TryReserveAuxiliaryMemory("one", 60, out var a), Is.True);
        Assert.That(scheduler.TryReserveAuxiliaryMemory("two", 50, out _), Is.False);
        Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.EqualTo(60));
        Assert.That(scheduler.TryReserveAuxiliaryMemory("two", 40, out var b), Is.True);
        Assert.That(scheduler.TryReleaseAuxiliaryMemory("two", a), Is.False);
        Assert.That(scheduler.TryReleaseAuxiliaryMemory("one", a), Is.True);
        Assert.That(scheduler.TryReleaseAuxiliaryMemory("one", a), Is.False);
        Assert.That(scheduler.TryGetDeviceUsage("shared", out var usage), Is.True);
        Assert.That(usage.ReservedMemoryBytes, Is.EqualTo(2 * reservation + 40));
        scheduler.Remove("two"); Assert.That(scheduler.TryReleaseAuxiliaryMemory("two", b), Is.False);
        Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.Zero);
        Assert.That(scheduler.ReservedMemoryBytes, Is.EqualTo(reservation));
    }

    [Test]
    public void AuxiliaryResizeIsAtomicAcrossPrincipalsAndDoesNotNeedAnotherLeaseSlot()
    {
        var one = Identity(); var reserved = one.MemoryReservationBytes;
        var scheduler = new MetaScheduler(continuationPolicy: new() { AuxiliaryLeasesPerProcess = 1 });
        scheduler.TryConfigureDevice("shared", new(2, reserved * 2 + 100, 1000, 262144));
        scheduler.TryRegister("one", "alice", "shared", one); scheduler.TryRegister("two", "bob", "shared", Identity());
        scheduler.TryReserveAuxiliaryMemory("one", 40, out var a); scheduler.TryReserveAuxiliaryMemory("two", 60, out var b);
        Assert.That(scheduler.TryReserveAuxiliaryMemory("one", 1, out _), Is.False);
        Assert.That(scheduler.TryResizeAuxiliaryMemory("one", a, 80), Is.False);
        Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.EqualTo(100));
        Assert.That(scheduler.TryResizeAuxiliaryMemory("two", a, 1), Is.False);
        Assert.That(scheduler.TryReleaseAuxiliaryMemory("two", b), Is.True);
        Assert.That(scheduler.TryResizeAuxiliaryMemory("one", a, 80), Is.True);
        Assert.That(scheduler.TryGetDeviceUsage("shared", out var usage), Is.True);
        Assert.That(usage.ReservedMemoryBytes, Is.EqualTo(reserved * 2 + 80));
        Assert.That(scheduler.TryResizeAuxiliaryMemory("one", a, 20), Is.True);
        Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.EqualTo(20));
        Assert.That(scheduler.TryResizeAuxiliaryMemory("one", a, 0), Is.True);
        Assert.That(scheduler.TryResizeAuxiliaryMemory("one", a, 10), Is.False);
        Assert.That(scheduler.ReservedMemoryBytes, Is.EqualTo(reserved * 2));
    }

    [Test]
    public void InvalidNativeQuotesFaultWithoutSpendingUnquotedGuestGas()
    {
        var calls = new Calls(1) { Cost = 300000 };
        var process = Identity(); var scheduler = Scheduler(process, calls);
        Assert.That(scheduler.RunTick(0, 10000).GasUsed, Is.Zero);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.InternalFault)); Assert.That(calls.Started, Is.Zero);
    }

    [Test]
    public void RecursiveTickIsRejectedWithoutGrantingAnotherBudget()
    {
        var calls = new Calls(1); var scheduler = Scheduler(Identity(), calls);
        calls.OnExecute = (_, _) => Assert.Throws<InvalidOperationException>(() => scheduler.RunTick(0, 10000));
        Assert.That(scheduler.RunTick(0, 18).GasUsed, Is.EqualTo(18));
        Assert.That(calls.Results.Count, Is.EqualTo(1));
    }

    [Test]
    public void HeapResultSurvivesIncrementalCompletionAndAnEnqueuedWatch()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.continuations; import meta.doors; link door:Door=\"door\"; fn view(value:string)->string{return value;} watch door.stateChanged(state){}")], "test.continuations"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var module = compiled.Module!;
        var artifact = MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module));
        var process = new MetaProcess(artifact, limits: new() { MemoryBytes = 65536, StringBytes = 8192 });
        var calls = new Calls(1) { Function = Array.FindIndex(module.Functions, function => function.Name.EndsWith(".view", StringComparison.Ordinal)), CompletionChunks = 3 };
        calls.Argument = (p, _) => { for (var index = 0; index < 11; index++) p.CreateString(new string('x', 1000)); return p.CreateString(new string('z', 6000)); };
        var scheduler = Scheduler(process, calls); var chunks = 0;
        calls.OnExecute = (p, kind) =>
        {
            if (kind != MetaContinuationKind.CompleteInvocation) return;
            Assert.That(p.GetString(p.LastResult!.Value), Is.EqualTo(new string('z', 6000)));
            if (++chunks != 1) return;
            var stamp = p.CreateRecord(p.GetTypeId("meta.system.SampleStamp"), [MetaValue.Int(1), MetaValue.Int(1), MetaValue.Int(1)]);
            var state = p.CreateRecord(p.GetTypeId("meta.doors.DoorState"), [MetaValue.Bool(false), p.CreateEnum(p.GetTypeId("meta.doors.DoorPhase"), 0), stamp]);
            Assert.That(scheduler.TryEnqueueEvent("app", 0, [state], 1), Is.True);
            Assert.That(p.State, Is.EqualTo(MetaProcessState.Idle)); Assert.That(p.QueuedEvents, Is.EqualTo(1));
        };
        var tick = scheduler.RunTick(0, 10000);
        Assert.That(chunks, Is.EqualTo(3)); Assert.That(calls.Results.Count, Is.EqualTo(1));
        Assert.That(process.QueuedEvents, Is.Zero); Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
        Assert.That(tick.Slices, Is.GreaterThan(2), "GC work and the separate watch must both be scheduled.");
    }

    [Test]
    public void DebugPauseBlocksTheNextGuestSliceAndKeepsItsResultBarrier()
    {
        var process = Identity(); var calls = new Calls(2); var scheduler = Scheduler(process, calls);
        Assert.That(scheduler.RunTick(0, 8).ContinuationSteps, Is.EqualTo(1));
        Assert.That(process.TryPause(0), Is.True);
        scheduler.SetClock(1, 2, 16); Assert.That(scheduler.RunTick(16, 1000).GasUsed, Is.Zero);
        Assert.That(calls.Started, Is.EqualTo(1)); Assert.That(calls.Results, Is.Empty);
        Assert.That(process.TryContinue(), Is.True); scheduler.NotifyRunnable("app");
        scheduler.SetClock(1, 3, 16); scheduler.RunTick(32, 1000);
        Assert.That(calls.Results.Select(result => result.AsInt()), Is.EqualTo(new[] { 1, 2 }));
    }

    private sealed class AwaitHost : IMetaHost
    {
        public int Import;
        public int Cancels;
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException();
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) { Import = request.ImportId; return MetaHostResult.Awaiting(42); }
        public void Log(MetaProcess process, string text) { }
        public void Cancel(MetaProcess process, long operationId) => Cancels++;
    }

    [Test]
    public void WaitingInvocationResumesOnlyTheExactStagedHostCompletion()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.pending; import meta.doors; link door:Door=\"door\"; async fn view(value:int)->int{let response=await door.setOpen(true); return value;}")], "test.pending"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var module = compiled.Module!; var host = new AwaitHost();
        var process = new MetaProcess(MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module)), host);
        process.TryBindLink(0, 100);
        var calls = new Calls(2); var scheduler = Scheduler(process, calls);
        scheduler.RunTick(0, 1000);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Waiting)); Assert.That(calls.Started, Is.EqualTo(1));
        scheduler.SetClock(1, 2, 16); scheduler.NotifyContinuation("app");
        Assert.That(scheduler.RunTick(16, 1000).GasUsed, Is.Zero);
        var result = process.CreateErrorResult(module.Imports[host.Import].ReturnType!.Value, "Busy");
        Assert.That(scheduler.QueueCompletion("app", 41, result), Is.False);
        Assert.That(scheduler.QueueCompletion("app", 42, result), Is.True);
        Assert.That(scheduler.QueueCompletion("app", 42, result), Is.False);
        scheduler.SetClock(1, 3, 16); var tick = scheduler.RunTick(32, 1000);
        Assert.That(tick.Completions, Is.EqualTo(1)); Assert.That(calls.Results.Select(value => value.AsInt()), Is.EqualTo(new[] { 1 }));
        Assert.That(calls.Started, Is.EqualTo(2)); Assert.That(process.State, Is.EqualTo(MetaProcessState.Waiting));
        scheduler.QueueStop("app", MetaStopReason.PermissionRevoked); scheduler.SetClock(1, 4, 16); scheduler.RunTick(48, 1000);
        Assert.That(host.Cancels, Is.EqualTo(1)); Assert.That(calls.Cancelled, Is.EqualTo(1));
    }

    [Test]
    public void CallbackFailureAndCancellationFailureStillReleaseEveryLeaseOnce()
    {
        var process = Identity(); var calls = new Calls(1); var scheduler = Scheduler(process, calls);
        Assert.That(scheduler.TryReserveAuxiliaryMemory("app", 500, out var lease), Is.True);
        calls.OnComplete = () => throw new InvalidOperationException("Failed native completion.");
        calls.OnCancel = () => throw new InvalidOperationException("Failed native cleanup.");
        Assert.DoesNotThrow(() => scheduler.RunTick(0, 1000));
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.InternalFault)); Assert.That(calls.Cancelled, Is.EqualTo(1));
        Assert.That(scheduler.AuxiliaryReservedMemoryBytes, Is.Zero); Assert.That(scheduler.TryReleaseAuxiliaryMemory("app", lease), Is.False);
        scheduler.Remove("app"); Assert.That(scheduler.ReservedMemoryBytes, Is.Zero);
    }

    [Test]
    public void ZeroQuotesHaveAPolicyMinimumAndSmallTickBudgetsCannotGetFreeSteps()
    {
        var calls = new Calls(50) { Cost = 0 }; var scheduler = Scheduler(Identity(), calls);
        Assert.That(MetaContinuationPolicy.Version, Is.EqualTo("meta.host-work/1"));
        Assert.That(scheduler.RunTick(0, 7).GasUsed, Is.Zero); Assert.That(calls.Started, Is.Zero);
        scheduler.SetClock(1, 2, 16);
        var tick = scheduler.RunTick(16, 18);
        Assert.That(tick.ContinuationGasUsed, Is.EqualTo(16)); Assert.That(tick.GasUsed, Is.EqualTo(18));
        Assert.That(calls.Results.Count, Is.EqualTo(1));
    }

    [Test]
    public void FaultCancellationMayRemoveAnotherPendingCompletionWithoutInvalidatingTheDrain()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.pending; import meta.doors; link door:Door=\"door\"; async fn view(value:int)->int{let response=await door.setOpen(true); return value;}")], "test.pending"));
        Assert.That(compiled.Success, Is.True);
        var module = compiled.Module!;
        var artifact = MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module));
        var first = new MetaProcess(artifact, new AwaitHost()); var second = new MetaProcess(artifact, new AwaitHost());
        first.TryBindLink(0, 100); second.TryBindLink(0, 100);
        var one = new Calls(1); var two = new Calls(1); var scheduler = Scheduler(first, one);
        scheduler.TryRegister("two", "other", "other", second, continuations: two); scheduler.NotifyContinuation("two");
        scheduler.RunTick(0, 1000);
        Assert.That(first.State, Is.EqualTo(MetaProcessState.Waiting)); Assert.That(second.State, Is.EqualTo(MetaProcessState.Waiting));
        one.OnCancel = () => scheduler.Remove("two");
        Assert.That(scheduler.QueueCompletion("app", 42, MetaValue.Unit), Is.True);
        Assert.That(scheduler.QueueCompletion("two", 42, MetaValue.Unit), Is.True);
        scheduler.SetClock(1, 2, 16);
        Assert.DoesNotThrow(() => scheduler.RunTick(16, 1000));
        Assert.That(first.State, Is.EqualTo(MetaProcessState.Faulted)); Assert.That(second.State, Is.EqualTo(MetaProcessState.Stopped));
        Assert.That(scheduler.RegisteredProcesses, Is.EqualTo(1)); Assert.That(two.Cancelled, Is.EqualTo(1));
    }

    private sealed class Ingress : IMetaSchedulerContinuations
    {
        public int Executed;
        public Action? OnExecute;
        public bool TryPeek(string processId, out MetaContinuationStep step) { step = new(Executed + 1, MetaContinuationKind.ValidateIngress, 8); return Executed < 10; }
        public MetaContinuationResult Execute(string processId, MetaProcess process, in MetaContinuationStep step) { Executed++; OnExecute?.Invoke(); return MetaContinuationResult.Progress; }
        public void Cancel(string processId, MetaStopReason reason) { }
    }

    [Test]
    public void PendingIngressAlternatesWithAnOrderedStreamOfNativeWatches()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.arbitration; import meta.doors; link panel:DoorPanel=\"panel\"; watch panel.requested(request){}")], "test.arbitration"));
        Assert.That(compiled.Success, Is.True);
        var module = compiled.Module!;
        Assert.That(module.Watches[0].Delivery, Is.EqualTo("Ordered"));
        var process = new MetaProcess(MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module)));
        var scheduler = new MetaScheduler(); scheduler.SetClock(1, 1, 16); var ingress = new Ingress(); var sequence = 0;
        scheduler.TryRegister("app", "owner", "host", process, continuations: ingress); scheduler.NotifyContinuation("app");
        var firstIngress = -1; ingress.OnExecute = () => { if (firstIngress < 0) firstIngress = sequence; };
        scheduler.RunTick(0, 100, beforeSlice: _ =>
        {
            if (sequence >= 50 || process.State != MetaProcessState.Idle || process.QueuedEvents != 0) return true;
            var stamp = process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"), [MetaValue.Int(1), MetaValue.Int(++sequence), MetaValue.Int(1)]);
            var state = process.CreateRecord(process.GetTypeId("meta.doors.DoorRequest"), [MetaValue.Bool(false), process.CreateString("request"), stamp]);
            Assert.That(scheduler.TryEnqueueEvent("app", 0, [state], sequence), Is.True); return true;
        });
        Assert.That(ingress.Executed, Is.EqualTo(10)); Assert.That(firstIngress, Is.InRange(0, 2));
        Assert.That(sequence, Is.GreaterThan(1));
    }
}
