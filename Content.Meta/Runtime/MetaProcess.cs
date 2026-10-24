// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Diagnostics;
using Content.Meta.Verification;

namespace Content.Meta.Runtime;

/// <summary>One isolated guest heap and explicit frame stack. Host callbacks never execute guest frames recursively.</summary>
public sealed partial class MetaProcess
{
    private sealed class Frame
    {
        public required MetaFunctionDef Function;
        public required MetaValue[] Locals;
        public required MetaValue[] Stack;
        public int Pc;
        public int Count;
        public long Bytes;
    }

    private readonly VerifiedArtifact _artifact;
    private readonly MetaModule _module;
    private readonly IMetaHost _host;
    private readonly MetaProcessLimits _limits;
    private readonly MetaHeap _heap = new();
    private readonly List<Frame> _frames = [];
    private readonly MetaValue[] _globals;
    private readonly MetaValue[] _constants;
    private readonly Dictionary<int, (MetaValue Value, ulong Token)> _links = [];
    private readonly Dictionary<int, (MetaValue Value, ulong Token)> _opaque = [];
    private readonly Stack<int> _freeOpaque = [];
    private int _nextOpaqueId;
    private static long _nextOpaqueGeneration;
    private readonly uint _handleGeneration = NextIdentity(ref _nextGeneration);
    private static long _nextGeneration;
    private long _frameBytes;
    private long _baseBytes;
    private long _debugBytes;
    private long _activationGas;
    private long _activationStart = -1;
    private long _nowMs;
    private long _pendingId;
    private int _pendingResultType;
    private MetaValue[] _pendingArguments = [];
    private bool _initializing;
    private bool _initialized;
    private bool _bootSequence;
    private bool _bootStarted;
    private bool _running;
    private bool _releaseStarted;
    private bool _stoppedCleanup;
    private long _collectionThreshold;
    private int _logBytes;
    private long _clockEpoch;
    private long _clockTick;
    private double _millisecondsPerTick = 1000.0 / 60;
    private double _logTokens = 20;
    private long _logRefillMs;

    public MetaProcessState State { get; private set; } = MetaProcessState.Idle;
    public MetaFault? Fault { get; private set; }
    public MetaStopReason StopReason { get; private set; }
    public long MemoryBytes => checked(_baseBytes + _frameBytes + _heap.Bytes + _pendingArguments.Length * 16L + _eventBytes + _debugBytes + _hostRootBytes);
    public long AllocationChurnBytes => _heap.Churn;
    public long MemoryReservationBytes => checked(_limits.MemoryBytes + _artifact.ResidentMemoryBytes);
    private const int MaximumComparisonBytes = 65536;
    public long MaximumPrimitiveGas => Math.Max(4L + MaximumComparisonBytes / 8L, Math.Max(16L + _limits.CollectionItems,
        256L + 2L * _limits.CollectionItems + _limits.StringBytes / 8L + _limits.MapStringKeyBytes / 16L));
    public long ActivationGas => _activationGas;
    /// <summary>Monotonic yield signal for a trusted scheduler; it does not reset activation gas or deadlines.</summary>
    public ulong YieldVersion { get; private set; }
    public long PendingOperationId => _pendingId;
    public long? ActivationDeadlineMs => _activationStart < 0 ? null : _activationStart > long.MaxValue - _limits.ActivationMilliseconds ? long.MaxValue : _activationStart + _limits.ActivationMilliseconds;
    public string ArtifactDigest => _artifact.Digest;
    public MetaValue? LastResult { get; private set; }
    public long NowMs => _nowMs;
    public long DroppedLogs { get; private set; }
    /// <summary>Optional trusted instrumentation; observations never become guest values or admission decisions.</summary>
    public IMetaMetrics? Metrics { get; set; }
    /// <summary>Trusted observers may only mark bounded dirty state; UI work runs after the VM phase. True denotes a watch activation.</summary>
    public event Action<bool>? ActivationCompleted;
    private bool _activationIsWatch;
    internal event Action? Terminated;
    private bool _terminationNotified;
    private void NotifyTermination()
    {
        if (_terminationNotified || State is not (MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined)) return;
        _terminationNotified = true; Terminated?.Invoke();
    }

    public void SetClock(long epoch, long tick, double millisecondsPerTick)
    {
        if (epoch < 0 || tick < 0 || !double.IsFinite(millisecondsPerTick) || millisecondsPerTick <= 0) throw new ArgumentOutOfRangeException(nameof(tick));
        _clockEpoch = epoch; _clockTick = tick; _millisecondsPerTick = millisecondsPerTick;
    }

    public bool CheckDeadline(long nowMs)
    {
        if (DebugPauseDeadlineMs is { } pauseDeadline && nowMs > pauseDeadline) { Stop(MetaStopReason.DebugPauseTimeout); return true; }
        if (_activationStart < 0 || State is MetaProcessState.Idle or MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined || nowMs - _activationStart <= _limits.ActivationMilliseconds) return false;
        StopReason = MetaStopReason.Runaway;
        Fail(new MetaTrap(MetaVmFaultCode.GasLimit, "Activation deadline exceeded."));
        return true;
    }

    public MetaProcess(VerifiedArtifact artifact, IMetaHost? host = null, MetaProcessLimits? limits = null)
    {
        _artifact = artifact; _module = artifact.Module; _host = host ?? MetaNullHost.Instance; _limits = limits ?? new MetaProcessLimits();
        if (_limits.MemoryBytes < 0 || _limits.Frames is < 1 or > 64 || _limits.ActivationMilliseconds < 1 || _limits.ActivationGas < 1 || _limits.CollectionItems < 0 || _limits.StringBytes < 0 || _limits.MapStringKeyBytes < 0 || _limits.GcWork < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        _globals = new MetaValue[_module.Globals.Length]; _constants = new MetaValue[_module.Constants.Length];
        _initialized = _module.Manifest.InitializerFunctionId == null;
        _baseBytes = checked(512L + 16L * (_globals.Length + _constants.Length) + 128L * _module.Links.Length);
        _collectionThreshold = _limits.MemoryBytes * 3 / 4;
        try
        {
            if (_baseBytes > _limits.MemoryBytes) Trap(MetaVmFaultCode.HeapLimit, "Process tables exceed memory quota.");
            for (var index = 0; index < _constants.Length; index++) _constants[index] = LoadConstant(_module.Constants[index]);
        }
        catch (MetaTrap trap) { Fail(trap); }
    }

    public bool TryBindLink(int linkId, ulong hostToken)
    {
        if (_running || State is not MetaProcessState.Idle || linkId < 0 || linkId >= _module.Links.Length) return false;
        var type = _module.Links[linkId].CapabilityTypeId;
        _links[linkId] = (new MetaValue(MetaValueKind.Capability, type, ((ulong) _handleGeneration << 32) | (uint) linkId), hostToken);
        return true;
    }

    public bool TryGetHostToken(MetaValue capability, out ulong hostToken)
    {
        hostToken = 0;
        if (capability.Kind != MetaValueKind.Capability || (uint) (capability.Payload >> 32) != _handleGeneration) return false;
        if (!_links.TryGetValue((int) (uint) capability.Payload, out var link) || link.Value != capability) return false;
        hostToken = link.Token; return true;
    }

    public MetaValue CreateOpaque(int typeId, ulong hostToken)
    {
        if (_module.Types[typeId].Kind != MetaTypeKind.Opaque) Trap(MetaVmFaultCode.InvalidBytecode, "Not an opaque ABI type.");
        var fresh = _freeOpaque.Count == 0;
        var bytes = fresh ? 128 : 64; Preflight(bytes);
        var id = fresh ? _nextOpaqueId++ : _freeOpaque.Pop();
        var generation = NextIdentity(ref _nextOpaqueGeneration);
        var value = new MetaValue(MetaValueKind.Opaque, typeId, ((ulong) generation << 32) | (uint) id);
        _opaque.Add(id, (value, hostToken)); _baseBytes += bytes;
        return value;
    }
    public bool TryGetOpaqueToken(MetaValue value, out ulong hostToken)
    {
        hostToken = 0;
        if (value.Kind != MetaValueKind.Opaque || !_opaque.TryGetValue((int) (uint) value.Payload, out var entry) || entry.Value != value) return false;
        hostToken = entry.Token; return true;
    }
    public bool TryReleaseOpaque(MetaValue value)
    {
        if (!TryGetOpaqueToken(value, out _)) return false;
        var id = (int) (uint) value.Payload; _opaque.Remove(id); _freeOpaque.Push(id); _baseBytes -= 64;
        return true;
    }

    public bool TryStartMain()
    {
        if (_bootStarted || State != MetaProcessState.Idle || _frames.Count != 0 || _links.Count != _module.Links.Length) return false;
        _bootStarted = true;
        _bootSequence = true;
        if (_module.Manifest.InitializerFunctionId is { } initializer)
        {
            _initializing = true;
            return TryStart(initializer, []);
        }
        if (_module.Manifest.MainFunctionId is { } main) return TryStart(main, []);
        if (_module.Watches.Length == 0 && !_limits.RetainForInvocations) { State = MetaProcessState.Stopped; StopReason = MetaStopReason.Completed; }
        NotifyTermination();
        _bootSequence = false;
        return true;
    }

    public bool TryStart(int functionId, IReadOnlyList<MetaValue> arguments)
    {
        if (_running || State != MetaProcessState.Idle || _frames.Count != 0 || functionId < 0 || functionId >= _module.Functions.Length) return false;
        var function = _module.Functions[functionId];
        if (function.Initializer ? _initialized : !_initialized) return false;
        if (arguments.Count != function.ParameterTypes.Length) return false;
        try
        {
            for (var index = 0; index < arguments.Count; index++) ValidateValue(arguments[index], function.ParameterTypes[index]);
            _activationGas = 0; _activationStart = -1; _logBytes = 0; LastResult = null; _activationIsWatch = false;
            ReleaseCurrentEventAuthority();
            if (function.Initializer) _initializing = true;
            PushFrame(functionId, arguments);
            State = MetaProcessState.Runnable;
            return true;
        }
        catch (MetaTrap trap) { Fail(trap); return false; }
    }

    public MetaSliceResult RunSlice(int gas, long nowMs, int instructionLimit = 4096, TimeSpan? wallLimit = null)
    {
        using var measured = MetaMetricScope.Measure(Metrics, MetaMetric.VmSliceElapsed, _clockTick);
        if (gas <= 0 || instructionLimit <= 0 || nowMs < 0 || _running) throw new ArgumentOutOfRangeException(nameof(gas));
        if (State is not (MetaProcessState.Runnable or MetaProcessState.Collecting)) return new MetaSliceResult(State, 0, 0, Fault, StopReason);
        _nowMs = nowMs;
        if (DebugPaused) { CheckDeadline(nowMs); return new MetaSliceResult(State, 0, 0, Fault, StopReason); }
        if (_activationStart < 0) _activationStart = nowMs;
        var used = 0; var count = 0;
        var started = Stopwatch.GetTimestamp();
        _running = true;
        try
        {
            if (_nowMs - _activationStart > _limits.ActivationMilliseconds) { StopReason = MetaStopReason.Runaway; Trap(MetaVmFaultCode.GasLimit, "Activation deadline exceeded."); }
            if (State == MetaProcessState.Collecting)
            {
                using var collection = MetaMetricScope.Measure(Metrics, MetaMetric.GuestGcElapsed, _clockTick);
                used = _heap.CollectSlice(Math.Min(gas, instructionLimit), _limits.GcWork);
                ChargeActivation(used);
                if (!_heap.Collecting)
                {
                    State = _frames.Count == 0 ? MetaProcessState.Idle : MetaProcessState.Runnable;
                    _collectionThreshold = Math.Min(_limits.MemoryBytes, Math.Max(_limits.MemoryBytes * 3 / 4, MemoryBytes + Math.Max(256, _limits.MemoryBytes / 16)));
                }
                return new MetaSliceResult(State, used, 0, Fault, StopReason);
            }
            State = MetaProcessState.Running;
            while (State == MetaProcessState.Running && used < gas && count < instructionLimit)
            {
                if (wallLimit is { } ceiling && Stopwatch.GetElapsedTime(started) >= ceiling) break;
                var frame = _frames[^1];
                var instruction = _artifact.Instructions[frame.Function.Id][frame.Pc];
                if (BreakBeforeInstruction(frame, instruction)) break;
                var cost = Cost(frame, instruction);
                // One bounded primitive can exceed the slice; its debt is reported to the scheduler.
                if (used != 0 && cost > gas - used) break;
                ChargeActivation(cost);
                used = checked(used + cost); count++; frame.Pc++;
                Execute(frame, instruction);
                BreakAfterInstruction();
                if (State == MetaProcessState.Running && MemoryBytes >= _collectionThreshold && _heap.Bytes > 0)
                {
                    using var collection = MetaMetricScope.Measure(Metrics, MetaMetric.GuestGcElapsed, _clockTick);
                    _heap.BeginCollection(Roots()); State = MetaProcessState.Collecting;
                }
            }
            if (State == MetaProcessState.Running) State = MetaProcessState.Runnable;
        }
        catch (MetaTrap trap) { Fail(trap); }
        catch (Exception exception) when (exception is OverflowException or DivideByZeroException)
        { Fail(new MetaTrap(MetaVmFaultCode.NumericFault, "Checked numeric operation failed.")); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { Fail(new MetaTrap(MetaVmFaultCode.InternalFault, "Runtime invariant failed: " + exception.GetType().Name)); }
        finally { _running = false; NotifyTermination(); }
        return new MetaSliceResult(State, used, count, Fault, StopReason);
    }

    public bool TryCompleteHost(long operationId, MetaValue value)
    {
        if (_running || State != MetaProcessState.Waiting || operationId == 0 || operationId != _pendingId) return false;
        try
        {
            ValidateValue(value, _pendingResultType);
            if (_pendingResultType != 0) Push(_frames[^1], value);
            _pendingId = 0; Array.Clear(_pendingArguments); _pendingArguments = [];
            State = MetaProcessState.Runnable;
            return true;
        }
        catch (MetaTrap trap) { Fail(trap); return false; }
    }

    public void Stop(MetaStopReason reason = MetaStopReason.Stop)
    {
        if (_stoppedCleanup) return;
        _stoppedCleanup = true;
        if (State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined && StopReason != MetaStopReason.None) reason = StopReason;
        if (_pendingId != 0)
        {
            try { _host.Cancel(this, _pendingId); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { Fault ??= new MetaFault(MetaVmFaultCode.InternalFault, "Host cancellation failed."); }
            finally { _pendingId = 0; }
        }
        StopReason = reason; State = MetaProcessState.Stopped;
        foreach (var lease in _hostRoots) lease?.Dispose();
        ReleaseCurrentEventAuthority();
        _links.Clear();
        _baseBytes -= (_opaque.Count + (long) _nextOpaqueId) * 64L; _opaque.Clear(); _freeOpaque.Clear(); _nextOpaqueId = 0;
        foreach (var frame in _frames) { Array.Clear(frame.Locals); Array.Clear(frame.Stack); }
        _frames.Clear(); _frameBytes = 0; Array.Clear(_globals); Array.Clear(_constants); Array.Clear(_pendingArguments); _pendingArguments = [];
        foreach (var queued in _events) { ReleaseEventAuthority(queued.AuthorityToken); ReleaseEventContext(queued.ContextToken); Array.Clear(queued.Arguments); }
        _events.Clear(); _eventBytes = 0;
        _heap.RestartCollection([]);
        NotifyTermination();
    }

    /// <summary>Bounded cleanup for stopped/faulted processes. No frame can execute during release.</summary>
    public bool ReleaseSlice(int work = 256)
    {
        if (State is not (MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined)) return false;
        using var collection = MetaMetricScope.Measure(Metrics, MetaMetric.GuestGcElapsed, _clockTick);
        if (!_releaseStarted)
        {
            var state = State; Stop(StopReason); State = state; _releaseStarted = true;
        }
        if (!_heap.Collecting) _heap.BeginCollection([]);
        _heap.CollectSlice(Math.Max(1, work), int.MaxValue);
        return !_heap.Collecting;
    }

    public MetaValue CreateString(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid Unicode string.");
            }
            else if (char.IsLowSurrogate(value[index])) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid Unicode string.");
        }
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > _limits.StringBytes) Trap(MetaVmFaultCode.HeapLimit, "String byte limit.");
        return Allocate(5, new MetaHeapObject { Kind = MetaValueKind.String, Text = value, Bytes = checked(32L + value.Length * 2L + bytes) });
    }

    public string GetString(MetaValue value) => _heap.Get(value).Text ?? throw new InvalidOperationException("Value is not string.");
    public MetaValue CreateEnum(int typeId, int tag)
    {
        var type = _module.Types[typeId];
        if (type.Kind != MetaTypeKind.Enum || !type.Variants.Any(variant => variant.Tag == tag)) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid enum value.");
        return new MetaValue(MetaValueKind.Enum, typeId, checked((ulong) tag));
    }

    public MetaValue CreateRecord(int typeId, IReadOnlyList<MetaValue> fields)
    {
        var type = _module.Types[typeId];
        if (type.Kind is not (MetaTypeKind.Struct or MetaTypeKind.NativeDto) || fields.Count != type.Fields.Length) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid record shape.");
        Preflight(checked(160L + fields.Count * 16L));
        for (var index = 0; index < fields.Count; index++) ValidateValue(fields[index], type.Fields[index].TypeId);
        return Allocate(typeId, new MetaHeapObject { Kind = MetaValueKind.Record, Values = fields.ToArray(), Count = fields.Count, Bytes = checked(32L + fields.Count * 16L) });
    }

    public MetaValue GetField(MetaValue record, int fieldId) => _heap.Get(record).Values[fieldId];
    public MetaValue CreateArray(int typeId, IReadOnlyList<MetaValue> items)
    {
        var type = _module.Types[typeId];
        if (type.Kind != MetaTypeKind.Array || items.Count > _limits.CollectionItems) Trap(MetaVmFaultCode.HeapLimit, "Array size or type.");
        Preflight(checked(160L + 16L * items.Count));
        for (var index = 0; index < items.Count; index++) ValidateValue(items[index], type.Args[0]);
        return Allocate(typeId, new MetaHeapObject { Kind = MetaValueKind.Array, Values = items.ToArray(), Count = items.Count, Bytes = checked(32L + 16L * items.Count) });
    }
    public MetaValue CreateList(int typeId, IReadOnlyList<MetaValue> items)
    {
        var type = _module.Types[typeId];
        if (type.Kind != MetaTypeKind.List || items.Count > _limits.CollectionItems) Trap(MetaVmFaultCode.HeapLimit, "List size or type.");
        Preflight(checked(160L + 16L * items.Count));
        foreach (var item in items) ValidateValue(item, type.Args[0]);
        return Allocate(typeId, new MetaHeapObject { Kind = MetaValueKind.List, Values = items.ToArray(), Count = items.Count, Bytes = checked(32L + 16L * items.Count) });
    }
    /// <summary>Host decoding rejects duplicate keys. VM map.set separately retains ordinary replacement semantics.</summary>
    public MetaValue CreateMap(int typeId, IReadOnlyList<KeyValuePair<MetaValue, MetaValue>> items)
    {
        var type = _module.Types[typeId];
        if (type.Kind != MetaTypeKind.Map || items.Count > _limits.CollectionItems) Trap(MetaVmFaultCode.HeapLimit, "Map size or type.");
        Preflight(checked(192L + 192L * items.Count));
        var keys = new HashSet<MetaValue>(new MetaMapKeyComparer(this));
        long keyBytes = 0;
        foreach (var item in items)
        {
            ValidateValue(item.Key, type.Args[0]); ValidateValue(item.Value, type.Args[1]);
            if (!keys.Add(item.Key)) throw new ArgumentException("Duplicate map key.", nameof(items));
            keyBytes = checked(keyBytes + StringKeyBytes(item.Key));
            if (keyBytes > _limits.MapStringKeyBytes) Trap(MetaVmFaultCode.HeapLimit, "Map string key byte limit.");
        }
        return Allocate(typeId, new MetaHeapObject { Kind = MetaValueKind.Map, Entries = items.ToList(), Count = items.Count, KeyStringBytes = keyBytes, Bytes = checked(64L + 64L * items.Count) });
    }
    private int StringKeyBytes(MetaValue key) => key.Kind == MetaValueKind.String ? Encoding.UTF8.GetByteCount(GetString(key)) : 0;
    private static uint NextIdentity(ref long counter)
    {
        var value = Interlocked.Increment(ref counter);
        if (value is <= 0 or > uint.MaxValue) throw new MetaTrap(MetaVmFaultCode.InternalFault, "Process identity space exhausted.");
        return (uint) value;
    }
    public MetaValue[] GetArrayCopy(MetaValue array) => _heap.Get(array).Values.ToArray();
    public MetaValue[] GetSequenceCopy(MetaValue sequence, int maximumItems = 4096)
    {
        var obj = _heap.Get(sequence);
        if (sequence.Kind is not (MetaValueKind.Array or MetaValueKind.List) || maximumItems is < 0 or > 65536 || obj.Count > maximumItems) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        return obj.Values.AsSpan(0, obj.Count).ToArray();
    }
    public KeyValuePair<MetaValue, MetaValue>[] GetMapEntriesCopy(MetaValue map, int maximumItems = 4096)
    {
        var obj = _heap.Get(map);
        if (map.Kind != MetaValueKind.Map || maximumItems is < 0 or > 65536 || obj.Count > maximumItems) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        return obj.Entries!.ToArray();
    }
    public MetaValue CreateVariant(int typeId, int tag, MetaValue? payload = null)
    {
        var type = _module.Types[typeId];
        var variant = type.Variants.SingleOrDefault(candidate => candidate.Tag == tag);
        if (variant == null || type.Kind is not (MetaTypeKind.Option or MetaTypeKind.Result)) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid variant shape.");
        if (variant!.PayloadTypeId is { } expected)
        {
            if (payload == null) Trap(MetaVmFaultCode.InvalidBytecode, "Missing variant payload.");
            ValidateValue(payload!.Value, expected);
        }
        else if (payload != null) Trap(MetaVmFaultCode.InvalidBytecode, "Unexpected variant payload.");
        return Allocate(typeId, new MetaHeapObject { Kind = MetaValueKind.Variant, Tag = tag, Values = payload is { } item ? [item] : [], Count = payload == null ? 0 : 1, Bytes = payload == null ? 16 : 32 });
    }
    public int GetVariantTag(MetaValue value) => value.Kind == MetaValueKind.Enum ? (int) value.Payload : _heap.Get(value).Tag;
    public MetaValue GetVariantPayload(MetaValue value) => _heap.Get(value).Values[0];

    public MetaDebugSnapshot GetDebugSnapshot(int maximumValues = 64)
    {
        maximumValues = Math.Clamp(maximumValues, 0, 256);
        var bytes = 128 + 16 * 128 + maximumValues * 384L;
        Preflight(bytes); _debugBytes += bytes;
        var remaining = maximumValues; var frames = new List<MetaDebugFrame>();
        foreach (var frame in _frames.TakeLast(16))
        {
            var locals = frame.Locals.Take(remaining).Select(DescribeValue).ToArray(); remaining -= locals.Length;
            var operands = frame.Stack.Take(Math.Min(frame.Count, remaining)).Select(DescribeValue).ToArray(); remaining -= operands.Length;
            frames.Add(new MetaDebugFrame(frame.Function.Id, frame.Pc < _artifact.Instructions[frame.Function.Id].Length ? _artifact.Instructions[frame.Function.Id][frame.Pc].Offset : frame.Function.CodeLength, locals, operands));
        }
        return new MetaDebugSnapshot(State, MemoryBytes, _activationGas, frames.ToArray(), () => _debugBytes -= bytes);
    }

    private void Execute(Frame frame, MetaInstruction i)
    {
        var a = checked((int) i.A); var b = unchecked((int) i.B); var op = i.Opcode;
        MetaValue Pop() => PopValue(frame);
        void Put(MetaValue value) => Push(frame, value);
        void Jump(uint target) => frame.Pc = _artifact.InstructionIndices[frame.Function.Id][checked((int) target)];
        switch (op)
        {
            case MetaOpcode.Const: Put(_constants[a]); break;
            case MetaOpcode.Unit: Put(MetaValue.Unit); break;
            case MetaOpcode.LoadLocal: Put(frame.Locals[a]); break;
            case MetaOpcode.StoreLocal: frame.Locals[a] = Pop(); break;
            case MetaOpcode.ResetLocal: frame.Locals[a] = default; break;
            case MetaOpcode.LoadGlobal: Put(_globals[a]); break;
            case MetaOpcode.StoreGlobal: _globals[a] = Pop(); break;
            case MetaOpcode.Pop: Pop(); break;
            case MetaOpcode.Dup: Put(frame.Stack[frame.Count - 1]); break;
            case >= MetaOpcode.IAdd and <= MetaOpcode.IShr:
                var right = Pop().AsInt();
                if (op is MetaOpcode.INeg or MetaOpcode.INot) { Put(MetaValue.Int(op == MetaOpcode.INeg ? checked(-right) : ~right)); break; }
                var left = Pop().AsInt();
                if (op is MetaOpcode.IDiv or MetaOpcode.IRem && right == 0) Trap(MetaVmFaultCode.NumericFault, "Integer division by zero.");
                if (op is MetaOpcode.IShl or MetaOpcode.IShr && right is < 0 or > 63) Trap(MetaVmFaultCode.NumericFault, "Shift outside 0..63.");
                Put(MetaValue.Int(op switch
                {
                    MetaOpcode.IAdd => checked(left + right), MetaOpcode.ISub => checked(left - right), MetaOpcode.IMul => checked(left * right),
                    MetaOpcode.IDiv => left == long.MinValue && right == -1 ? throw new MetaTrap(MetaVmFaultCode.NumericFault, "Integer division overflow.") : left / right,
                    MetaOpcode.IRem => left == long.MinValue && right == -1 ? 0 : left % right,
                    MetaOpcode.IAnd => left & right, MetaOpcode.IOr => left | right, MetaOpcode.IXor => left ^ right,
                    MetaOpcode.IShl => left << (int) right, MetaOpcode.IShr => left >> (int) right,
                    _ => throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Invalid integer opcode.")
                })); break;
            case >= MetaOpcode.FAdd and <= MetaOpcode.FNeg:
                var r = Pop().AsFloat();
                if (op == MetaOpcode.FNeg) { Put(Float(-r)); break; }
                var l = Pop().AsFloat();
                if (op is MetaOpcode.FDiv or MetaOpcode.FRem && r == 0) Trap(MetaVmFaultCode.NumericFault, "Float division by zero.");
                Put(Float(op switch { MetaOpcode.FAdd => l + r, MetaOpcode.FSub => l - r, MetaOpcode.FMul => l * r, MetaOpcode.FDiv => l / r, MetaOpcode.FRem => l % r, _ => throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Invalid float opcode.") })); break;
            case >= MetaOpcode.Eq and <= MetaOpcode.Ge:
                var rv = Pop(); var lv = Pop(); var comparison = Compare(lv, rv);
                Put(MetaValue.Bool(op switch { MetaOpcode.Eq => comparison == 0, MetaOpcode.Ne => comparison != 0, MetaOpcode.Lt => comparison < 0, MetaOpcode.Le => comparison <= 0, MetaOpcode.Gt => comparison > 0, _ => comparison >= 0 })); break;
            case MetaOpcode.Not: Put(MetaValue.Bool(!Pop().AsBool())); break;
            case MetaOpcode.Br: Jump(i.A); break;
            case MetaOpcode.BrFalse: if (!Pop().AsBool()) Jump(i.A); break;
            case MetaOpcode.BrVariant: if (GetVariantTag(frame.Stack[frame.Count - 1]) == b) Jump(i.C); break;
            case MetaOpcode.Call or MetaOpcode.CallAsync:
                var callee = _module.Functions[a]; var args = new MetaValue[callee.ParameterTypes.Length];
                for (var index = args.Length - 1; index >= 0; index--) args[index] = Pop();
                PushFrame(a, args); Array.Clear(args); break;
            case MetaOpcode.Return:
                var returned = frame.Function.ReturnType != 0 ? Pop() : MetaValue.Unit;
                Array.Clear(frame.Locals); Array.Clear(frame.Stack); _frameBytes -= frame.Bytes; _frames.RemoveAt(_frames.Count - 1);
                if (_frames.Count != 0) { if (frame.Function.ReturnType != 0) Push(_frames[^1], returned); }
                else if (_initializing)
                {
                    _initializing = false; _initialized = true; _activationGas = 0; _activationStart = -1;
                    if (_bootSequence && _module.Manifest.MainFunctionId is { } main) PushFrame(main, []);
                    else { State = _bootSequence && _module.Watches.Length == 0 && !_limits.RetainForInvocations ? MetaProcessState.Stopped : MetaProcessState.Idle; _bootSequence = false; }
                }
                else
                {
                    LastResult = returned; State = _bootSequence && _module.Watches.Length == 0 && !_limits.RetainForInvocations ? MetaProcessState.Stopped : MetaProcessState.Idle;
                    ReleaseCurrentEventAuthority();
                    if (State == MetaProcessState.Stopped) StopReason = MetaStopReason.Completed;
                    _bootSequence = false;
                    var completedWatch = _activationIsWatch; _activationIsWatch = false; ActivationCompleted?.Invoke(completedWatch);
                }
                break;
            case MetaOpcode.RecordNew:
                var fields = new MetaValue[_module.Types[a].Fields.Length]; for (var index = fields.Length - 1; index >= 0; index--) fields[index] = Pop(); Put(CreateRecord(a, fields)); break;
            case MetaOpcode.RecordGet: Put(GetField(Pop(), b)); break;
            case MetaOpcode.RecordSet:
                var fieldValue = Pop(); var record = Pop(); var changed = _heap.Get(record).Values.ToArray(); changed[b] = fieldValue; Put(CreateRecord(a, changed)); break;
            case MetaOpcode.VariantNew:
                var variant = GetVariantDefinition(a, b);
                Put(_module.Types[a].Kind == MetaTypeKind.Enum ? CreateEnum(a, b) : CreateVariant(a, b, variant.PayloadTypeId != null ? Pop() : null)); break;
            case MetaOpcode.VariantTag: Put(MetaValue.Int(GetVariantTag(Pop()))); break;
            case MetaOpcode.VariantPayload: var variantValue = Pop(); if (GetVariantTag(variantValue) != b) Trap(MetaVmFaultCode.InvalidBytecode, "Wrong variant tag."); Put(GetVariantPayload(variantValue)); break;
            case MetaOpcode.ArrayNew:
                var fill = Pop(); var length = Pop().AsInt(); CheckLength(length); Preflight(checked(160 + length * 16));
                var array = new MetaValue[(int) length]; Array.Fill(array, fill); Put(Allocate(a, new MetaHeapObject { Kind = MetaValueKind.Array, Values = array, Count = array.Length, Bytes = checked(32 + length * 16) })); break;
            case MetaOpcode.ArrayLen: Put(MetaValue.Int(_heap.Get(Pop()).Count)); break;
            case MetaOpcode.ArrayGet: var at = Pop().AsInt(); var source = _heap.Get(Pop()); Put(source.Values[CheckIndex(at, source.Count)]); break;
            case MetaOpcode.ArraySet: var item = Pop(); var position = Pop().AsInt(); var target = _heap.Get(Pop()); target.Values[CheckIndex(position, target.Count)] = item; break;
            case MetaOpcode.ArrayLiteral:
                CheckLength(b); Preflight(checked(160L + b * 16L)); var elements = new MetaValue[b]; for (var index = b - 1; index >= 0; index--) elements[index] = Pop(); Put(Allocate(a, new MetaHeapObject { Kind = MetaValueKind.Array, Values = elements, Count = b, Bytes = checked(32L + b * 16L) })); break;
            case MetaOpcode.CallIntrinsic: ExecuteIntrinsic(frame, i.A, i.B); break;
            case MetaOpcode.LoadLink: if (!_links.TryGetValue(a, out var link)) Trap(MetaVmFaultCode.InvalidBytecode, "Unbound required link."); Put(link.Value); break;
            case MetaOpcode.HostRead or MetaOpcode.HostAwait:
                var import = _module.Imports[a]; var arguments = new MetaValue[import.ParameterTypes.Length];
                for (var index = arguments.Length - 1; index >= 0; index--) arguments[index] = Pop();
                var capability = Pop(); if (!TryGetHostToken(capability, out _)) Trap(MetaVmFaultCode.InvalidBytecode, "Foreign capability.");
                var request = new MetaHostRequest(a, capability, arguments);
                if (op == MetaOpcode.HostRead)
                {
                    var result = _host.Read(this, request); ValidateValue(result, import.ReturnType!.Value); if (import.ReturnType != 0) Put(result);
                }
                else
                {
                    Preflight(checked(64L + arguments.Length * 16L));
                    var result = _host.BeginAwait(this, request);
                    if (result.Pending)
                    {
                        if (result.OperationId == 0 || _pendingId != 0) Trap(MetaVmFaultCode.InternalFault, "Invalid host operation ID.");
                        _pendingId = result.OperationId; _pendingResultType = import.ReturnType!.Value; _pendingArguments = arguments; State = MetaProcessState.Waiting;
                    }
                    else { ValidateValue(result.Value, import.ReturnType!.Value); if (import.ReturnType != 0) Put(result.Value); }
                }
                break;
            case MetaOpcode.Yield: YieldVersion = unchecked(YieldVersion + 1); State = MetaProcessState.Runnable; break;
            case MetaOpcode.Safepoint: break;
            case MetaOpcode.Trap: Trap((MetaVmFaultCode) a, "Explicit VM trap."); break;
            default: Trap(MetaVmFaultCode.InvalidBytecode, "Unknown opcode."); break;
        }
    }

    private void PushFrame(int functionId, IReadOnlyList<MetaValue> args)
    {
        var function = _module.Functions[functionId];
        var slots = checked(function.Locals.Length + function.MaxStack);
        if (_frames.Count >= _limits.Frames || slots + _frames.Sum(frame => frame.Locals.Length + frame.Stack.Length) > _limits.StackSlots) Trap(MetaVmFaultCode.StackLimit, "Frame/slot limit.");
        var bytes = checked(128L + 16L * slots);
        Preflight(bytes);
        var next = new Frame { Function = function, Locals = new MetaValue[function.Locals.Length], Stack = new MetaValue[function.MaxStack], Bytes = bytes };
        for (var index = 0; index < args.Count; index++) next.Locals[index] = args[index];
        _frames.Add(next); _frameBytes += bytes;
    }

    private static MetaValue PopValue(Frame frame)
    {
        if (frame.Count <= 0) throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Operand stack underflow.");
        var value = frame.Stack[--frame.Count]; frame.Stack[frame.Count] = default; return value;
    }
    private static void Push(Frame frame, MetaValue value)
    {
        if (frame.Count >= frame.Stack.Length) throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Operand stack overflow.");
        frame.Stack[frame.Count++] = value;
    }
    private MetaValue Allocate(int typeId, MetaHeapObject value)
    {
        if (_heap.Collecting) Trap(MetaVmFaultCode.HeapLimit, "Cannot mutate a collecting process heap.");
        return _heap.Allocate(typeId, value, _limits.MemoryBytes - MemoryBytes, _limits.AllocationChurnBytes);
    }
    private void Preflight(long bytes) { if (bytes < 0 || bytes > _limits.MemoryBytes - MemoryBytes) Trap(MetaVmFaultCode.HeapLimit, "Process memory limit."); }
    private void ChargeActivation(int gas) { if (gas > _limits.ActivationGas - _activationGas) { StopReason = MetaStopReason.Runaway; Trap(MetaVmFaultCode.GasLimit, "Activation gas exceeded."); } _activationGas += gas; }
    private void CheckLength(long length) { if (length < 0 || length > _limits.CollectionItems) Trap(MetaVmFaultCode.BoundsFault, "Collection length limit."); }
    private static int CheckIndex(long index, int count) => index >= 0 && index < count ? (int) index : throw new MetaTrap(MetaVmFaultCode.BoundsFault, "Collection index out of bounds.");
    private static MetaValue Float(double value) => double.IsFinite(value) ? MetaValue.Float(value) : throw new MetaTrap(MetaVmFaultCode.NumericFault, "Nonfinite float.");
    private static void Trap(MetaVmFaultCode code, string detail) => throw new MetaTrap(code, detail);
    private void Fail(MetaTrap trap)
    {
        Fault = new MetaFault(trap.Code, trap.Message);
        if (trap.Message == "GcWorkExceeded") StopReason = MetaStopReason.GcWorkExceeded;
        if (trap.Code == MetaVmFaultCode.InternalFault) StopReason = MetaStopReason.InternalFault;
        State = MetaProcessState.Faulted;
        ReleaseCurrentEventAuthority();
        if (_pendingId != 0)
        {
            try { _host.Cancel(this, _pendingId); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { StopReason = MetaStopReason.InternalFault; }
            _pendingId = 0; Array.Clear(_pendingArguments); _pendingArguments = [];
        }
        NotifyTermination();
    }
    private IEnumerable<MetaValue> Roots()
    {
        foreach (var value in _constants) yield return value;
        foreach (var value in _globals) yield return value;
        foreach (var frame in _frames)
        {
            foreach (var value in frame.Locals) yield return value;
            for (var index = 0; index < frame.Count; index++) yield return frame.Stack[index];
        }
        foreach (var value in _pendingArguments) yield return value;
        // Fixed slots remain valid if a native owner disposes its lease during incremental GC.
        for (var slot = 0; slot < _hostRoots.Length; slot++)
            if (_hostRoots[slot] is { } lease)
                for (var index = 0; index < lease.Count; index++) yield return lease.GetValue(index);
        foreach (var queued in _events) foreach (var value in queued.Arguments) yield return value;
        if (LastResult is { } result) yield return result;
    }
    private void ValidateValue(MetaValue value, int expected)
    {
        if (value.TypeId != expected) Trap(MetaVmFaultCode.InvalidBytecode, "Foreign or mistyped host value.");
        var kind = _module.Types[expected].Kind;
        var valid = kind switch
        {
            MetaTypeKind.Unit => value.Kind == MetaValueKind.Unit && value.Payload == 0,
            MetaTypeKind.Bool => value.Kind == MetaValueKind.Bool && value.Payload <= 1,
            MetaTypeKind.Byte => value.Kind == MetaValueKind.Byte && value.Payload <= 255,
            MetaTypeKind.Int => value.Kind == MetaValueKind.Int,
            MetaTypeKind.Float => value.Kind == MetaValueKind.Float && double.IsFinite(value.AsFloat()) && value.Payload != 0x8000000000000000,
            MetaTypeKind.Enum => value.Kind == MetaValueKind.Enum && _module.Types[expected].Variants.Any(variant => (ulong) variant.Tag == value.Payload),
            MetaTypeKind.Capability => TryGetHostToken(value, out _),
            MetaTypeKind.Opaque => TryGetOpaqueToken(value, out _),
            MetaTypeKind.Struct or MetaTypeKind.NativeDto => value.Kind == MetaValueKind.Record,
            MetaTypeKind.Option or MetaTypeKind.Result => value.Kind == MetaValueKind.Variant,
            _ => value.Kind.ToString() == kind.ToString()
        };
        if (!valid) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid host value representation.");
        if (value.Kind is >= MetaValueKind.String and <= MetaValueKind.Map) _heap.Get(value);
    }
    private MetaValue LoadConstant(MetaConstantDef constant) => constant.Kind switch
    {
        MetaConstantKind.Unit => MetaValue.Unit,
        MetaConstantKind.Bool => MetaValue.Bool(constant.Value == "true"),
        MetaConstantKind.Byte => MetaValue.Byte(byte.Parse(constant.Value!, CultureInfo.InvariantCulture)),
        MetaConstantKind.Int => MetaValue.Int(long.Parse(constant.Value!, CultureInfo.InvariantCulture)),
        MetaConstantKind.Float => Float(BitConverter.UInt64BitsToDouble(ulong.Parse(constant.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))),
        MetaConstantKind.String => CreateString(constant.Value!),
        MetaConstantKind.Enum => CreateEnum(constant.TypeId, int.Parse(constant.Value!, CultureInfo.InvariantCulture)),
        MetaConstantKind.Struct => CreateRecord(constant.TypeId, constant.Items.Select(index => _constants[index]).ToArray()),
        _ => throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Forbidden constant.")
    };
    private int Compare(MetaValue left, MetaValue right)
    {
        if (left == right) return 0;
        return left.Kind switch
        {
        MetaValueKind.String => CompareScalars(GetString(left), GetString(right)),
        MetaValueKind.Int => left.AsInt().CompareTo(right.AsInt()),
        MetaValueKind.Float => left.AsFloat().CompareTo(right.AsFloat()),
        MetaValueKind.Record => CompareRecords(left, right),
        MetaValueKind.Variant => CompareVariants(left, right),
        _ => left.Payload.CompareTo(right.Payload)
        };
    }
    private sealed class MetaMapKeyComparer(MetaProcess process) : IEqualityComparer<MetaValue>
    {
        private readonly Dictionary<MetaValue, int> _hashes = [];
        public bool Equals(MetaValue left, MetaValue right) => left.TypeId == right.TypeId && process.Compare(left, right) == 0;
        public int GetHashCode(MetaValue value)
        {
            if (_hashes.TryGetValue(value, out var hash)) return hash;
            hash = value.Kind == MetaValueKind.String ? StringComparer.Ordinal.GetHashCode(process.GetString(value)) : value.GetHashCode();
            _hashes.Add(value, hash); return hash;
        }
    }

    private int CompareRecords(MetaValue left, MetaValue right)
    {
        var leftFields = _heap.Get(left).Values; var rightFields = _heap.Get(right).Values;
        for (var index = 0; index < leftFields.Length; index++) { var comparison = Compare(leftFields[index], rightFields[index]); if (comparison != 0) return comparison; }
        return 0;
    }
    private int CompareVariants(MetaValue left, MetaValue right)
    {
        var l = _heap.Get(left); var r = _heap.Get(right);
        var tagComparison = l.Tag.CompareTo(r.Tag);
        return tagComparison != 0 ? tagComparison : l.Count == 0 ? 0 : Compare(l.Values[0], r.Values[0]);
    }
    private MetaVariantDef GetVariantDefinition(int typeId, int tag)
    {
        foreach (var variant in _module.Types[typeId].Variants) if (variant.Tag == tag) return variant;
        throw new MetaTrap(MetaVmFaultCode.InvalidBytecode, "Unknown variant tag.");
    }

    private static int CompareScalars(string left, string right)
    {
        var leftRunes = left.EnumerateRunes(); var rightRunes = right.EnumerateRunes();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext(); var hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight) return hasLeft.CompareTo(hasRight);
            var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (comparison != 0) return comparison;
        }
    }
}

public sealed record MetaDebugFrame(int FunctionId, int InstructionOffset, MetaDebugValue[] Locals, MetaDebugValue[] Operands);
public sealed class MetaDebugSnapshot : IDisposable
{
    private Action? _release;
    public MetaProcessState State { get; }
    public long MemoryBytes { get; }
    public long ActivationGas { get; }
    public MetaDebugFrame[] Frames { get; }
    internal MetaDebugSnapshot(MetaProcessState state, long memoryBytes, long activationGas, MetaDebugFrame[] frames, Action release)
    { State = state; MemoryBytes = memoryBytes; ActivationGas = activationGas; Frames = frames; _release = release; }
    public void Dispose() { Interlocked.Exchange(ref _release, null)?.Invoke(); }
}
