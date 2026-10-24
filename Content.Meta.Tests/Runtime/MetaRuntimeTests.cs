// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Runtime.InteropServices;
using Content.Meta.Bytecode;
using Content.Meta.Runtime;
using Content.Meta.Tests.Bytecode;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaRuntimeTests
{
    [Test]
    public void SharedRecordGraphCannotExpandOneComparisonBeyondItsAdvertisedPrimitiveBudget()
    {
        var types = MetaTypeDef.Primitives.ToList();
        for (var layer = 0; layer < 3; layer++)
        {
            var recordId = types.Count;
            types.Add(new() { Id = recordId, Kind = MetaTypeKind.Struct, Name = "test.Record" + layer,
                Fields = Enumerable.Range(0, 64).Select(index => new MetaFieldDef("f" + index, layer == 0 ? 3 : recordId - 1, true)).ToArray() });
            if (layer < 2) types.Add(new() { Id = recordId + 1, Kind = MetaTypeKind.Option, Name = "test.Option" + layer,
                Args = [recordId], Variants = [new("none", 0, null), new("some", 1, recordId)] });
        }
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.LoadLocal, 1),
            new(0, MetaOpcode.Eq, 10), new(0, MetaOpcode.Return)], result: 1, maximum: 2) with { Types = types.ToArray() };
        module = module with { Functions = [module.Functions[0] with { ParameterTypes = [10, 10],
            Locals = [new(10, false, true), new(10, false, true)] }] };
        var process = new MetaProcess(MetaBytecodeTests.Verify(module), limits: new() { MemoryBytes = 8 * 1024 * 1024 });
        MetaValue Root()
        {
            var leaf = process.CreateRecord(6, Enumerable.Repeat(MetaValue.Int(1), 64).ToArray());
            var first = process.CreateVariant(7, 1, leaf);
            var middle = process.CreateRecord(8, Enumerable.Repeat(first, 64).ToArray());
            var second = process.CreateVariant(9, 1, middle);
            return process.CreateRecord(10, Enumerable.Repeat(second, 64).ToArray());
        }
        var left = Root(); var right = Root();
        Assert.That(process.MemoryBytes, Is.LessThan(16384));
        Assert.That(process.TryStart(0, [left, left]), Is.True);
        Assert.That(process.RunSlice(16, 0).GasUsed, Is.LessThanOrEqualTo(16));
        Assert.That(process.LastResult!.Value.AsBool(), Is.True);
        Assert.That(process.TryStart(0, [left, right]), Is.True);
        for (var slice = 0; slice < 4 && process.State == MetaProcessState.Runnable; slice++)
            Assert.That(process.RunSlice(1, 0).GasUsed, Is.LessThanOrEqualTo(process.MaximumPrimitiveGas));
        Assert.That(process.State, Is.EqualTo(MetaProcessState.Faulted));
        Assert.That(process.Fault!.Code, Is.EqualTo(MetaVmFaultCode.HeapLimit));
        Assert.That(process.Fault.Detail, Is.EqualTo("Comparison work limit."));
        var minimal = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Return)])),
            limits: new() { CollectionItems = 0, StringBytes = 0, MapStringKeyBytes = 0 });
        Assert.That(minimal.MaximumPrimitiveGas, Is.GreaterThanOrEqualTo(8196));
    }

    [Test]
    public void AdvertisedMinimumAccountsForUtf8ConstantsAndInitialFrameBeforeStarting()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Pop), new(0, MetaOpcode.Return)],
            [new MetaConstantDef { Kind = MetaConstantKind.String, TypeId = 5, Value = "Ж🚀" }], maximum: 1);
        var artifact = MetaBytecodeTests.Verify(module);
        var process = new MetaProcess(artifact, limits: new() { MemoryBytes = artifact.MinimumProcessMemoryBytes });
        Assert.That(artifact.MaximumConstantStringBytes, Is.EqualTo(6));
        Assert.That(process.TryStart(0, []), Is.True);
        Assert.That(process.MemoryBytes, Is.EqualTo(artifact.MinimumProcessMemoryBytes));
        var insufficient = new MetaProcess(artifact, limits: new() { MemoryBytes = artifact.MinimumProcessMemoryBytes - 1 });
        Assert.That(insufficient.TryStart(0, []), Is.False);
        Assert.That(insufficient.State, Is.EqualTo(MetaProcessState.Faulted));
        Assert.That(insufficient.Fault!.Code, Is.EqualTo(MetaVmFaultCode.HeapLimit));
    }

    [Test]
    public void RepeatedTemporaryAllocationsKeepReclaimingBelowHeapCapacity()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Safepoint), new(0, MetaOpcode.Br, 0)]);
        var process = new MetaProcess(MetaBytecodeTests.Verify(module), limits: new MetaProcessLimits { MemoryBytes = 4096 });
        process.TryStart(0, []);
        for (var round = 0; round < 32; round++)
        {
            for (var item = 0; item < 6; item++) process.CreateString(new string('x', 100));
            process.RunSlice(1, round);
            Assert.That(process.State, Is.EqualTo(MetaProcessState.Collecting));
            for (var step = 0; step < 1000 && process.State == MetaProcessState.Collecting; step++) process.RunSlice(32, round);
            Assert.That(process.State, Is.EqualTo(MetaProcessState.Runnable));
            Assert.That(process.MemoryBytes, Is.LessThan(2048));
        }
    }

    [Test]
    public void RepeatedStopPreservesTerminalReasonAndNaturalCompletionStillReleasesHeap()
    {
        var process = Process(MetaBytecodeTests.Module([new(0, MetaOpcode.Return)]));
        process.Stop(MetaStopReason.PermissionRevoked); process.Stop(MetaStopReason.Stop);
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Return)]);
        module = module with { Manifest = module.Manifest with { MainFunctionId = 0 } };
        var completed = new MetaProcess(MetaBytecodeTests.Verify(module)); completed.CreateString(new string('x', 2048));
        completed.TryStartMain(); completed.RunSlice(100, 0);
        var before = completed.MemoryBytes;
        for (var step = 0; step < 1000 && !completed.ReleaseSlice(16); step++) { }
        Assert.That(completed.MemoryBytes, Is.LessThan(before));
        Assert.That(completed.StopReason, Is.EqualTo(MetaStopReason.Completed));
    }

    private static MetaModule MapFunction(bool write)
    {
        var code = new MetaInstruction[] { new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.LoadLocal, 1) };
        if (write) code = [.. code, new(0, MetaOpcode.LoadLocal, 2)];
        code = [.. code, new(0, MetaOpcode.CallIntrinsic, write ? 0x0112u : 0x0111u, 6), new(0, MetaOpcode.Return)];
        var module = MetaBytecodeTests.Module(code, result: write ? 0 : 7, maximum: write ? 3 : 2) with
        { Types = [.. MetaTypeDef.Primitives, new() { Id = 6, Kind = MetaTypeKind.Map, Name = "test.map", Args = [5, 3] },
            new() { Id = 7, Kind = MetaTypeKind.Option, Name = "test.option", Args = [3], Variants = [new("none", 0, null), new("some", 1, 3)] }] };
        int[] parameters = write ? [6, 5, 3] : [6, 5];
        return module with { Functions = [module.Functions[0] with { ParameterTypes = parameters, Locals = parameters.Select(type => new MetaLocalDef(type, false, true)).ToArray() }] };
    }

    [Test]
    public void StringMapLookupChargesAccumulatedKeyBytesAndInsertionQuotaIsAtomic()
    {
        int Lookup(int prefix)
        {
            var process = new MetaProcess(MetaBytecodeTests.Verify(MapFunction(false)));
            var pairs = Enumerable.Range(0, 4).Select(index => new KeyValuePair<MetaValue, MetaValue>(process.CreateString(new string('a', prefix) + index), MetaValue.Int(index))).ToArray();
            var map = process.CreateMap(6, pairs); var query = process.CreateString(new string('a', prefix) + "3");
            process.TryStart(0, [map, query]); return process.RunSlice(10000, 0).GasUsed;
        }
        Assert.That(Lookup(200), Is.GreaterThan(Lookup(0) + 30));
        var limited = new MetaProcess(MetaBytecodeTests.Verify(MapFunction(true)), limits: new MetaProcessLimits { MapStringKeyBytes = 512 });
        var existing = limited.CreateString(new string('a', 300)); var incoming = limited.CreateString(new string('b', 300));
        var target = limited.CreateMap(6, [new(existing, MetaValue.Int(1))]);
        limited.TryStart(0, [target, incoming, MetaValue.Int(2)]); limited.RunSlice(10000, 0);
        Assert.That(limited.Fault?.Code, Is.EqualTo(MetaVmFaultCode.HeapLimit));
        Assert.That(limited.GetMapEntriesCopy(target), Has.Length.EqualTo(1));
    }

    private static MetaProcess Process(MetaModule module, MetaProcessLimits? limits = null)
    {
        var process = new MetaProcess(MetaBytecodeTests.Verify(module), limits: limits);
        process.TryStart(0, []);
        return process;
    }
    private static void Drain(MetaProcess process, int slice = 32, int maximum = 10000)
    {
        for (var iteration = 0; iteration < maximum && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; iteration++) process.RunSlice(slice, 0);
    }

    [Test]
    public void MetaValueIsExactlySixteenBytesWithoutClrReference()
    {
        Assert.That(Marshal.SizeOf<MetaValue>(), Is.EqualTo(16));
        Assert.That(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<MetaValue>(), Is.False);
    }

    [TestCase(MetaOpcode.IDiv, long.MinValue, -1, MetaVmFaultCode.NumericFault)]
    [TestCase(MetaOpcode.IAdd, long.MaxValue, 1, MetaVmFaultCode.NumericFault)]
    [TestCase(MetaOpcode.IDiv, 2, 0, MetaVmFaultCode.NumericFault)]
    [TestCase(MetaOpcode.IShl, 2, 64, MetaVmFaultCode.NumericFault)]
    public void NumericBoundariesTrapOnlyTheProcess(MetaOpcode op, long left, long right, MetaVmFaultCode fault)
    {
        var process = Process(MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, op), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(left), MetaBytecodeTests.Int(right)], 3, 2));
        Drain(process);
        Assert.That(process.Fault?.Code, Is.EqualTo(fault));
    }

    [TestCase(MetaOpcode.IRem, long.MinValue, -1, 0)]
    [TestCase(MetaOpcode.IDiv, -7, 3, -2)]
    [TestCase(MetaOpcode.IRem, -7, 3, -1)]
    [TestCase(MetaOpcode.IShl, long.MaxValue, 1, -2)]
    [TestCase(MetaOpcode.IShr, -8, 2, -2)]
    public void IntegerRulesMatchNormativeSemantics(MetaOpcode op, long left, long right, long expected)
    {
        var process = Process(MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, op), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(left), MetaBytecodeTests.Int(right)], 3, 2));
        Drain(process);
        Assert.That(process.LastResult?.AsInt(), Is.EqualTo(expected));
    }

    [Test]
    public void GuestRecursionUsesExplicitFramesAndStopsAtFrameLimit()
    {
        var process = Process(MetaBytecodeTests.Module([new(0, MetaOpcode.Call), new(0, MetaOpcode.Return)]), new MetaProcessLimits { Frames = 8 });
        Drain(process);
        Assert.That(process.Fault?.Code, Is.EqualTo(MetaVmFaultCode.StackLimit));
    }

    [Test]
    public void YieldDoesNotResetActivationGas()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Yield), new(0, MetaOpcode.Br, 0)]);
        module = module with { Functions = [module.Functions[0] with { Async = true }] };
        var process = Process(module, new MetaProcessLimits { ActivationGas = 10 });
        Drain(process);
        Assert.That(process.Fault?.Code, Is.EqualTo(MetaVmFaultCode.GasLimit));
        Assert.That(process.StopReason, Is.EqualTo(MetaStopReason.Runaway));
    }

    [Test]
    public void StructUpdateCopiesValueWhileArrayFieldsKeepLocalAliases()
    {
        var module = MetaBytecodeTests.Module([
            new(0, MetaOpcode.Const), new(0, MetaOpcode.RecordNew, 6), new(0, MetaOpcode.StoreLocal, 0),
            new(0, MetaOpcode.LoadLocal, 0), new(0, MetaOpcode.StoreLocal, 1),
            new(0, MetaOpcode.LoadLocal, 0), new(0, MetaOpcode.Const, 1), new(0, MetaOpcode.RecordSet, 6, 0), new(0, MetaOpcode.StoreLocal, 0),
            new(0, MetaOpcode.LoadLocal, 1), new(0, MetaOpcode.RecordGet, 6, 0), new(0, MetaOpcode.Return)],
            [MetaBytecodeTests.Int(7), MetaBytecodeTests.Int(99)], 3, 2, [new(6, true, false), new(6, false, false)]) with
        { Types = [.. MetaTypeDef.Primitives, new MetaTypeDef { Id = 6, Name = "Pair", Kind = MetaTypeKind.Struct, Fields = [new MetaFieldDef("n", 3, false)] }] };
        var process = Process(module); Drain(process);
        Assert.That(process.LastResult?.AsInt(), Is.EqualTo(7));
    }

    [Test]
    public void ProcessObjectHandlesCannotBeTransferredBetweenProcesses()
    {
        var artifact = MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.Return)], result: 5, maximum: 1,
            locals: [new MetaLocalDef(5, false, true)]) with { Functions = [new MetaFunctionDef { Id = 0, Name = "test.echo", ReturnType = 5, ParameterTypes = [5], Locals = [new(5, false, true)], MaxStack = 1, CodeLength = 6 }] });
        var first = new MetaProcess(artifact); var second = new MetaProcess(artifact);
        var firstText = first.CreateString("secret"); second.CreateString("other");
        Assert.That(second.TryStart(0, [firstText]), Is.False);
        Assert.That(second.Fault?.Code, Is.EqualTo(MetaVmFaultCode.InvalidBytecode));
    }

    [Test]
    public void LargeAllocationFailsBeforeAllocatingGuestArray()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, MetaOpcode.ArrayNew, 6), new(0, MetaOpcode.Pop), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(65536), MetaBytecodeTests.Int(0)], maximum: 2) with
        { Types = [.. MetaTypeDef.Primitives, new MetaTypeDef { Id = 6, Kind = MetaTypeKind.Array, Name = "array<int>", Args = [3] }] };
        var process = Process(module, new MetaProcessLimits { MemoryBytes = 4096 }); Drain(process);
        Assert.That(process.Fault?.Code, Is.EqualTo(MetaVmFaultCode.HeapLimit));
        Assert.That(process.MemoryBytes, Is.LessThanOrEqualTo(4096));
    }

    [Test]
    public void CrossProcessCopyPreservesReceiverAliasesWithoutSharingMutableStorage()
    {
        var module = MetaBytecodeTests.Module([
            new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.Const), new(0, MetaOpcode.Const, 1), new(0, MetaOpcode.ArraySet, 6), new(0, MetaOpcode.Return)],
            [MetaBytecodeTests.Int(0), MetaBytecodeTests.Int(99)], maximum: 3, locals: [new(6, false, true)]);
        module = module with
        {
            Types = [.. MetaTypeDef.Primitives, new MetaTypeDef { Id = 6, Name = "array<int>", Kind = MetaTypeKind.Array, Args = [3] }, new MetaTypeDef { Id = 7, Name = "test.Pair", Kind = MetaTypeKind.Struct, Fields = [new("a", 6, false), new("b", 6, false)] }],
            Functions = [module.Functions[0] with { ParameterTypes = [6] }]
        };
        var artifact = MetaBytecodeTests.Verify(module); var source = new MetaProcess(artifact); var receiver = new MetaProcess(artifact);
        var original = source.CreateArray(6, [MetaValue.Int(1)]);
        var record = source.CreateRecord(7, [original, original]);
        var copied = receiver.CopyFrom(source, record);
        var first = receiver.GetField(copied, 0); var second = receiver.GetField(copied, 1);
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first, Is.Not.EqualTo(original));
        receiver.TryStart(0, [first]); Drain(receiver);
        Assert.That(receiver.GetArrayCopy(second)[0].AsInt(), Is.EqualTo(99));
        Assert.That(source.GetArrayCopy(original)[0].AsInt(), Is.EqualTo(1));
    }

    [Test]
    public void ReleasingLargeHeapObjectUsesBoundedIncrementalWorkAndRevokesItsHandle()
    {
        var module = MetaBytecodeTests.Module([new(0, MetaOpcode.Return)]) with
        { Types = [.. MetaTypeDef.Primitives, new MetaTypeDef { Id = 6, Kind = MetaTypeKind.Array, Name = "array<int>", Args = [3] }] };
        var process = new MetaProcess(MetaBytecodeTests.Verify(module));
        var value = process.CreateArray(6, Enumerable.Repeat(MetaValue.Int(1), 512).ToArray());
        var occupied = process.MemoryBytes; process.Stop(MetaStopReason.Stop);
        var steps = 0;
        while (!process.ReleaseSlice(1) && ++steps < 2048) { }
        Assert.That(steps, Is.GreaterThanOrEqualTo(512));
        Assert.That(steps, Is.LessThan(2048));
        Assert.That(process.MemoryBytes, Is.LessThan(occupied));
        Assert.Catch(() => process.GetArrayCopy(value));
    }

    [Test]
    public void GeneratedPureExpressionsAgreeWithIndependentAstOracleAcrossSlices()
    {
        var random = new Random(2045);
        for (var sample = 0; sample < 100; sample++)
        {
            var constants = new List<MetaConstantDef>(); var code = new List<MetaInstruction>(); var depth = 0; var maximum = 0;
            Expr Generate(int remaining) => remaining == 0 || random.Next(3) == 0 ? new Literal(random.Next(-10, 11)) : new Binary(random.Next(3), Generate(remaining - 1), Generate(remaining - 1));
            void Emit(Expr expression)
            {
                if (expression is Literal literal) { code.Add(new(0, MetaOpcode.Const, (uint) constants.Count)); constants.Add(MetaBytecodeTests.Int(literal.Value)); maximum = Math.Max(maximum, ++depth); return; }
                var binary = (Binary) expression; Emit(binary.Left); Emit(binary.Right); depth--;
                code.Add(new(0, binary.Operation switch { 0 => MetaOpcode.IAdd, 1 => MetaOpcode.ISub, _ => MetaOpcode.IMul }));
            }
            var tree = Generate(4); Emit(tree); code.Add(new(0, MetaOpcode.Return));
            var process = Process(MetaBytecodeTests.Module(code.ToArray(), constants.ToArray(), 3, maximum)); Drain(process, sample % 17 + 1);
            Assert.That(process.LastResult?.AsInt(), Is.EqualTo(tree.Evaluate()), "seed 2045 sample " + sample);
        }
    }
    private abstract record Expr { public abstract long Evaluate(); }
    private sealed record Literal(long Value) : Expr { public override long Evaluate() => Value; }
    private sealed record Binary(int Operation, Expr Left, Expr Right) : Expr
    { public override long Evaluate() => Operation switch { 0 => checked(Left.Evaluate() + Right.Evaluate()), 1 => checked(Left.Evaluate() - Right.Evaluate()), _ => checked(Left.Evaluate() * Right.Evaluate()) }; }
}
