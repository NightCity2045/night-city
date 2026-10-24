// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Tests.Bytecode;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaDebuggerTests
{
    private static MetaProcess Nested()
    {
        var first = MetaInstructionCodec.Encode([new(0, MetaOpcode.Call, 1), new(0, MetaOpcode.Return)]);
        var second = MetaInstructionCodec.Encode([new(0, MetaOpcode.Const), new(0, MetaOpcode.Return)]);
        var module = new MetaModule
        {
            Manifest = MetaBytecodeTests.Manifest(), Constants = [MetaBytecodeTests.Int(42)], Code = [.. first, .. second],
            Functions = [new MetaFunctionDef { Id = 0, Name = "root", ReturnType = 3, MaxStack = 1, CodeLength = first.Length },
                new MetaFunctionDef { Id = 1, Name = "callee", ReturnType = 3, MaxStack = 1, CodeOffset = first.Length, CodeLength = second.Length }],
            Debug = new MetaDebugInfo([new(0, "test.meta", new string('0', 64))], [new(0, 0, 0, 0, 10), new(1, 0, 0, 11, 10)], [])
        };
        var process = new MetaProcess(MetaBytecodeTests.Verify(module)); process.TryStart(0, []);
        return process;
    }

    [Test]
    public void BreakpointStepIntoAndOutTrackExplicitFrames()
    {
        var process = Nested(); Assert.That(process.TrySetBreakpoint(0, 0), Is.True);
        Assert.That(process.RunSlice(100, 0).Instructions, Is.Zero);
        Assert.That(process.DebugPaused, Is.True);
        Assert.That(process.TryStepInto(), Is.True); process.RunSlice(100, 1);
        using (var snapshot = process.GetDebugSnapshot())
        {
            Assert.That(snapshot.Frames, Has.Length.EqualTo(2));
            Assert.That(snapshot.Frames[^1].FunctionId, Is.EqualTo(1));
            Assert.That(snapshot.Frames[^1].InstructionOffset, Is.Zero);
        }
        Assert.That(process.GetSourceSpan(1, 0)?.Utf8Start, Is.EqualTo(11));
        Assert.That(process.TryStepOut(), Is.True); process.RunSlice(100, 2);
        using (var snapshot = process.GetDebugSnapshot())
        {
            Assert.That(snapshot.Frames, Has.Length.EqualTo(1));
            Assert.That(snapshot.Frames[0].InstructionOffset, Is.EqualTo(6));
        }
        Assert.That(process.DebugPaused, Is.True);
        process.TryContinue(); process.RunSlice(100, 3);
        Assert.That(process.LastResult?.AsInt(), Is.EqualTo(42));
    }

    [Test]
    public void StepOverReturnsToInstructionFollowingCall()
    {
        var process = Nested(); process.TryPause(0); process.TryStepOver(); process.RunSlice(100, 1);
        using var snapshot = process.GetDebugSnapshot();
        Assert.That(snapshot.Frames, Has.Length.EqualTo(1));
        Assert.That(snapshot.Frames[0].InstructionOffset, Is.EqualTo(6));
        Assert.That(process.DebugPaused, Is.True);
        Assert.That(snapshot.Frames[0].Operands.Single().Display, Is.EqualTo("42"));
    }

    [Test]
    public void DebugSnapshotsReserveMemoryUntilDisposedAndNeverContainPayloadHandles()
    {
        var process = Nested(); var before = process.MemoryBytes;
        var snapshot = process.GetDebugSnapshot();
        Assert.That(process.MemoryBytes, Is.GreaterThan(before));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(snapshot), Does.Not.Contain("Payload"));
        snapshot.Dispose(); snapshot.Dispose();
        Assert.That(process.MemoryBytes, Is.EqualTo(before));
    }

    [Test]
    public void DebugPauseTimeoutRunsThroughReservedSchedulerTimer()
    {
        var process = Nested(); var scheduler = new MetaScheduler();
        scheduler.TryRegister("p", "owner", "device", process);
        process.TrySetBreakpoint(0, 0); scheduler.RunTick(0, 1000);
        Assert.That(process.DebugPaused, Is.True);
        Assert.That(scheduler.RunTick(1, 1000).Slices, Is.Zero);
        scheduler.RunTick(10001, 1000);
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Stopped));
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.DebugPauseTimeout));
    }

    [Test]
    public void BreakpointsMustPointAtVerifiedInstructionBoundary()
    {
        var process = Nested();
        Assert.That(process.TrySetBreakpoint(0, 2), Is.False);
        Assert.That(process.TrySetBreakpoint(99, 0), Is.False);
    }

    [Test]
    public void EvaluationIsReadOnlyBoundedAndRequiresPausedFrame()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Return)], locals: [new(3, false, true)]);
        module = module with
        {
            Functions = [module.Functions[0] with { ParameterTypes = [3] }],
            Debug = new MetaDebugInfo([], [], [new(0, 0, "number")])
        };
        var process = new MetaProcess(MetaBytecodeTests.Verify(module));
        Assert.That(process.TryStart(0, [MetaValue.Int(7)]), Is.True);
        Assert.That(process.TryEvaluateDebug("number + 3", out _), Is.False);
        process.TryPause(0); var memory = process.MemoryBytes; var gas = process.ActivationGas;
        Assert.That(process.TryEvaluateDebug("number * 6 == 42 && local0 == 7", out var value), Is.True);
        Assert.That(value, Is.EqualTo("true"));
        Assert.That(process.TryEvaluateDebug("-9223372036854775808 % -1", out value), Is.True);
        Assert.That(value, Is.EqualTo("0"));
        Assert.That(process.TryEvaluateDebug("false && (1 / 0 == 0)", out value), Is.True);
        Assert.That(value, Is.EqualTo("false"));
        Assert.That(process.TryEvaluateDebug("1e308 * 1e308 == 1e308 * 1e308", out _), Is.False);
        Assert.That(process.TryEvaluateDebug("log(\"side effect\")", out _), Is.False);
        Assert.That(process.TryEvaluateDebug("number = 1", out _), Is.False);
        Assert.That(process.TryEvaluateDebug("number + number", out _, 1), Is.False);
        Assert.That(process.TryEvaluateDebug(new string('1', 1025), out _), Is.False);
        Assert.That(process.MemoryBytes, Is.EqualTo(memory));
        Assert.That(process.ActivationGas, Is.EqualTo(gas));
        Assert.That(process.TryEvaluateDebug("number", out value), Is.True);
        Assert.That(value, Is.EqualTo("7"));
    }
}
