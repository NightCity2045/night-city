// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Tests.Bytecode;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaSchedulerTests
{
    private static MetaProcess Busy()
    {
        var process = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Br, 0)])), limits: new MetaProcessLimits { ActivationGas = 1_000_000_000, ActivationMilliseconds = 1_000_000 });
        Assert.That(process.TryStart(0, []), Is.True);
        return process;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeSliceGuardStopsPureComputationWithoutStarvingOtherPrincipals(bool throws)
    {
        var scheduler = new MetaScheduler(); var denied = Busy(); var allowed = Busy();
        scheduler.TryRegister("denied", "one", "one", denied);
        scheduler.TryRegister("allowed", "two", "two", allowed);
        var checkedIds = new List<string>();
        var result = scheduler.RunTick(0, 512, beforeSlice: id =>
        {
            checkedIds.Add(id);
            if (id != "denied") return true;
            if (throws) throw new InvalidOperationException("Native authority check failed.");
            return false;
        });
        Assert.That(checkedIds, Does.Contain("denied").And.Contain("allowed"));
        Assert.That(denied.ActivationGas, Is.Zero);
        Assert.That(denied.StopReason, Is.EqualTo(throws ? MetaStopReason.InternalFault : MetaStopReason.PermissionRevoked));
        Assert.That(allowed.ActivationGas, Is.EqualTo(512));
        Assert.That(result.ControlActions, Is.EqualTo(1));
        Assert.That(scheduler.TryDequeueTerminated(out var notice), Is.True);
        Assert.That(notice.ProcessId, Is.EqualTo("denied"));
        Assert.That(scheduler.TryDequeueTerminated(out _), Is.False);
    }

    [Test]
    public void PhysicalDeviceMemoryAndProcessCeilingsAreSharedAcrossPrincipals()
    {
        var reservation = Busy().MemoryReservationBytes;
        var scheduler = new MetaScheduler();
        var quota = new MetaDeviceQuota(2, reservation * 2, 1000, 262144);
        Assert.That(scheduler.TryConfigureDevice("physical", quota), Is.True);
        Assert.That(scheduler.TryRegister("one", "alice", "physical", Busy()), Is.True);
        Assert.That(scheduler.TryRegister("two", "bob", "physical", Busy()), Is.True);
        Assert.That(scheduler.TryRegister("three", "eve", "physical", Busy()), Is.False);
        Assert.That(scheduler.TryConfigureDevice("physical", quota with { MaxProcesses = 3 }), Is.False);
        Assert.That(scheduler.TryReleaseDevice("physical"), Is.False);
        Assert.That(scheduler.TryGetDeviceUsage("physical", out var usage), Is.True);
        Assert.That(usage.Processes, Is.EqualTo(2));
        Assert.That(usage.ReservedMemoryBytes, Is.EqualTo(reservation * 2));
        scheduler.Remove("one");
        Assert.That(scheduler.TryRegister("three", "eve", "physical", Busy()), Is.True);
        scheduler.Remove("two"); scheduler.Remove("three");
        Assert.That(scheduler.TryReleaseDevice("physical"), Is.True);
    }

    [Test]
    public void PhysicalGasWindowCannotBeMultipliedByChangingPrincipalOrRestartingProcesses()
    {
        var scheduler = new MetaScheduler();
        Assert.That(scheduler.TryConfigureDevice("physical", new(8, 16 * 1024 * 1024, 1000, 262144)), Is.True);
        scheduler.TryRegister("one", "alice", "physical", Busy());
        scheduler.TryRegister("two", "bob", "physical", Busy());
        Assert.That(scheduler.RunTick(0, 10000).GasUsed, Is.EqualTo(1000));
        scheduler.Remove("one"); scheduler.Remove("two");
        Assert.That(scheduler.TryRegister("three", "new-owner", "physical", Busy()), Is.True);
        Assert.That(scheduler.RunTick(999, 10000).GasUsed, Is.Zero);
        Assert.That(scheduler.RunTick(1000, 10000).GasUsed, Is.EqualTo(1000));
        Assert.That(scheduler.TryGetDeviceUsage("physical", out var usage), Is.True);
        Assert.That(usage.TotalGas, Is.EqualTo(2000));
    }

    [Test]
    public void HardwareThrottleOnlyReducesTheImmutableWindowAndCannotRaiseItsCeiling()
    {
        var scheduler = new MetaScheduler();
        scheduler.TryConfigureDevice("physical", new(1, 4 * 1024 * 1024, 1000, 262144));
        scheduler.TryRegister("one", "alice", "physical", Busy());
        Assert.That(scheduler.TrySetDeviceThrottle("physical", 250), Is.True);
        Assert.That(scheduler.TrySetDeviceThrottle("physical", 1001), Is.False);
        Assert.That(scheduler.RunTick(0, 10000).GasUsed, Is.EqualTo(250));
        scheduler.TrySetDeviceThrottle("physical", 0);
        Assert.That(scheduler.RunTick(1000, 10000).GasUsed, Is.Zero);
        scheduler.TrySetDeviceThrottle("physical", 1000);
        Assert.That(scheduler.RunTick(2000, 10000).GasUsed, Is.EqualTo(1000));
    }

    [Test]
    public void HardwareMemoryAdmissionIncludesArtifactResidency()
    {
        var process = Busy(); var scheduler = new MetaScheduler();
        scheduler.TryConfigureDevice("physical", new(1, process.MemoryReservationBytes - 1, 1000, 262144));
        Assert.That(scheduler.TryRegister("one", "alice", "physical", process), Is.False);
    }

    [Test]
    public void AdditionalProcessesDoNotMultiplyPrincipalGasShare()
    {
        var scheduler = new MetaScheduler(new MetaSchedulerLimits { PrincipalGasPerWindow = 10_000_000, DeviceGasPerWindow = 10_000_000, ProcessGasPerWindow = 10_000_000 });
        var crowded = Enumerable.Range(0, 8).Select(_ => Busy()).ToArray(); var other = Busy();
        for (var index = 0; index < crowded.Length; index++) Assert.That(scheduler.TryRegister("a" + index, "a", "device" + index, crowded[index]), Is.True);
        Assert.That(scheduler.TryRegister("b", "b", "device", other), Is.True);
        for (var tick = 0; tick < 20; tick++) scheduler.RunTick(tick, 4096);
        Assert.That(Math.Abs(crowded.Sum(process => process.ActivationGas) - other.ActivationGas), Is.LessThanOrEqualTo(256));
    }

    [Test]
    public void WindowQuotaIsSharedAndParkedPrincipalResumesAtNextWindow()
    {
        var scheduler = new MetaScheduler(new MetaSchedulerLimits { PrincipalGasPerWindow = 1000, DeviceGasPerWindow = 10000, ProcessGasPerWindow = 10000 });
        var first = Busy(); var second = Busy();
        scheduler.TryRegister("one", "owner", "a", first); scheduler.TryRegister("two", "owner", "b", second);
        scheduler.RunTick(0, 10000);
        Assert.That(first.ActivationGas + second.ActivationGas, Is.EqualTo(1000));
        scheduler.RunTick(999, 10000);
        Assert.That(first.ActivationGas + second.ActivationGas, Is.EqualTo(1000));
        scheduler.RunTick(1000, 10000);
        Assert.That(first.ActivationGas + second.ActivationGas, Is.EqualTo(2000));
    }

    [Test]
    public void ReservedStopPrecedesCommitAndNeverNeedsEventQueueCapacity()
    {
        var scheduler = new MetaScheduler(); var process = Busy(); scheduler.TryRegister("p", "owner", "device", process);
        scheduler.QueueStop("p", MetaStopReason.PowerLost);
        var committedAfterStop = false;
        var result = scheduler.RunTick(0, 1000, commitPreviousIntents: () => committedAfterStop = process.State == MetaProcessState.Stopped);
        Assert.That(committedAfterStop, Is.True);
        Assert.That(result.ControlActions, Is.EqualTo(1));
        Assert.That(result.GasUsed, Is.Zero);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.PowerLost));
    }

    [Test]
    public void SharedMemoryAdmissionReservesWholeProcessCapBeforeExecution()
    {
        var reservation = Busy().MemoryReservationBytes;
        var scheduler = new MetaScheduler(new MetaSchedulerLimits { ReservedMemoryBytes = reservation, PrincipalReservedMemoryBytes = reservation });
        Assert.That(scheduler.TryRegister("a", "a", "a", Busy()), Is.True);
        Assert.That(scheduler.TryRegister("b", "b", "b", Busy()), Is.False);
        scheduler.Remove("a");
        Assert.That(scheduler.TryRegister("b", "b", "b", Busy()), Is.True);
    }

    [Test]
    public void RevokedHashCannotRunOrBeReadmitted()
    {
        var scheduler = new MetaScheduler(); var process = Busy(); scheduler.TryRegister("p", "owner", "device", process);
        scheduler.RevokeArtifact(process.ArtifactDigest); scheduler.RunTick(0, 1000);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.ArtifactRevoked));
        Assert.That(scheduler.TryRegister("other", "other", "other", Busy()), Is.False);
    }

    [Test]
    public void HugeBoundedPrimitiveRunsOnceAndReportsGasDebtWithoutStarvation()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, MetaOpcode.ArrayNew, 6), new(0, MetaOpcode.Pop), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(2048), MetaBytecodeTests.Int(0)], maximum: 2) with
        { Types = [.. MetaTypeDef.Primitives, new MetaTypeDef { Id = 6, Kind = MetaTypeKind.Array, Name = "array<int>", Args = [3] }] };
        var process = new MetaProcess(MetaBytecodeTests.Verify(module)); process.TryStart(0, []);
        Assert.That(process.RunSlice(2, 0).GasUsed, Is.EqualTo(2));
        var result = process.RunSlice(1, 0);
        Assert.That(result.GasUsed, Is.EqualTo(2064));
        Assert.That(result.Instructions, Is.EqualTo(1));
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Runnable));
        process.RunSlice(2, 0);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
    }

    [Test]
    public void OversizedPrimitivePolicyIsRejectedAtSchedulerAdmission()
    {
        var process = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Return)])), limits: new MetaProcessLimits { CollectionItems = 1_000_000 });
        Assert.That(new MetaScheduler().TryRegister("p", "p", "d", process), Is.False);
    }

    [Test]
    public void RemovingProcessesPrunesOwnerBucketsAndPreservesStopReason()
    {
        var scheduler = new MetaScheduler(new MetaSchedulerLimits { Processes = 1 });
        for (var index = 0; index < 100; index++)
        {
            var process = Busy();
            Assert.That(scheduler.TryRegister("p", "owner" + index, "device" + index, process), Is.True);
            process.Stop(MetaStopReason.PermissionRevoked);
            Assert.That(scheduler.Remove("p"), Is.True);
            Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
            Assert.That(scheduler.RegisteredProcesses, Is.Zero);
            Assert.That(scheduler.RegisteredPrincipals, Is.Zero);
            Assert.That(scheduler.ReservedMemoryBytes, Is.Zero);
        }
        Assert.That(scheduler.RunTick(10000, 1000).Slices, Is.Zero);
    }

    [Test]
    public void AdmissionIncludesRetainedArtifactResidency()
    {
        var process = Busy();
        Assert.That(process.MemoryReservationBytes, Is.GreaterThan(1024 * 1024));
        var scheduler = new MetaScheduler(new MetaSchedulerLimits { ReservedMemoryBytes = process.MemoryReservationBytes - 1 });
        Assert.That(scheduler.TryRegister("p", "p", "d", process), Is.False);
    }

    [Test]
    public void TerminalNotificationsCoverDirectStopFaultAndNormalCompletionExactlyOnce()
    {
        var scheduler = new MetaScheduler(); var direct = Busy();
        scheduler.TryRegister("direct", "owner", "d", direct); direct.Stop(MetaStopReason.PermissionRevoked);
        Assert.That(scheduler.TryDequeueTerminated(out var notice), Is.True);
        Assert.That(notice.ProcessId, Is.EqualTo("direct")); Assert.That(notice.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
        direct.Stop(MetaStopReason.Stop); Assert.That(scheduler.TryDequeueTerminated(out _), Is.False);
        var faulted = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, MetaOpcode.IDiv), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(1), MetaBytecodeTests.Int(0)], 3, 2)));
        faulted.TryStart(0, []); scheduler.TryRegister("fault", "owner", "f", faulted); scheduler.RunTick(0, 1000);
        Assert.That(scheduler.TryDequeueTerminated(out notice), Is.True);
        Assert.That(notice.ProcessId, Is.EqualTo("fault")); Assert.That(notice.State, Is.EqualTo(MetaProcessState.Faulted)); Assert.That(notice.Fault?.Code, Is.EqualTo(MetaVmFaultCode.NumericFault));
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Return)]);
        module = module with { Manifest = module.Manifest with { MainFunctionId = 0 } };
        var completed = new MetaProcess(MetaBytecodeTests.Verify(module)); completed.TryStartMain(); scheduler.TryRegister("completed", "owner", "c", completed); scheduler.RunTick(1, 1000);
        Assert.That(scheduler.TryDequeueTerminated(out notice), Is.True);
        Assert.That(notice.StopReason, Is.EqualTo(MetaStopReason.Completed)); Assert.That(scheduler.TryDequeueTerminated(out _), Is.False);
    }
}
