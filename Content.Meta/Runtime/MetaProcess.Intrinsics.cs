// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Runtime;

public sealed partial class MetaProcess
{
    public int GetTypeId(string name) => Array.FindIndex(_module.Types, type => type.Name == name);
    public MetaImportDef GetImport(int id) => _artifact.GetImport(id);
    public MetaValue CreateError(string code, string message, long retryAfterMs = 0)
    {
        var error = GetTypeId("meta.system.Error"); var errorCode = GetTypeId("meta.system.ErrorCode");
        if (error < 0 || errorCode < 0 || retryAfterMs < 0) Trap(MetaVmFaultCode.InvalidBytecode, "Invalid Error factory arguments or ABI.");
        var variant = _module.Types[errorCode].Variants.SingleOrDefault(candidate => candidate.Name == code);
        if (variant == null) Trap(MetaVmFaultCode.InvalidBytecode, "Unknown ErrorCode.");
        var text = CreateString(TruncateUtf8(message, 512));
        return CreateRecord(error, [CreateEnum(errorCode, variant!.Tag), text, MetaValue.Int(retryAfterMs)]);
    }

    public MetaValue CreateResult(int resultTypeId, MetaValue value) => CreateVariant(resultTypeId, 0, value);
    public MetaValue CreateErrorResult(int resultTypeId, string errorCode, string message = "", long retryAfterMs = 0) => CreateVariant(resultTypeId, 1, CreateError(errorCode, message, retryAfterMs));

    private void ExecuteIntrinsic(Frame frame, uint intrinsicId, uint typeId)
    {
        MetaValue Pop() => PopValue(frame);
        void Put(MetaValue value) => Push(frame, value);
        var signature = _artifact.Intrinsics[(intrinsicId, typeId)];
        int ResultType() => signature.ReturnType;
        MetaValue Ok(MetaValue value) => CreateResult(ResultType(), value);
        MetaValue Error(string code) => CreateErrorResult(ResultType(), code);
        switch (intrinsicId)
        {
            case 0x0001:
                var logged = TruncateUtf8(GetString(Pop()), 1024); var logBytes = Encoding.UTF8.GetByteCount(logged);
                _logTokens = Math.Min(20, _logTokens + Math.Max(0, _nowMs - _logRefillMs) * 0.01); _logRefillMs = _nowMs;
                if (_logTokens >= 1 && logBytes <= _limits.LogBytes - _logBytes) { _logTokens--; _logBytes += logBytes; _host.Log(this, logged); }
                else DroppedLogs++;
                return;
            case 0x0002:
                var maxAge = Pop().AsInt(); var stamp = Pop(); var stampFields = _heap.Get(stamp).Values;
                var stampType = _module.Types[stamp.TypeId];
                var tickIndex = Array.FindIndex(stampType.Fields, field => field.Name == "tick");
                var epochIndex = Array.FindIndex(stampType.Fields, field => field.Name == "epoch");
                if (tickIndex < 0 || epochIndex < 0) Trap(MetaVmFaultCode.InvalidBytecode, "SampleStamp clock ABI.");
                var sampleTick = stampFields[tickIndex].AsInt();
                Put(MetaValue.Bool(maxAge is >= 0 and <= 60000 && stampFields[epochIndex].AsInt() == _clockEpoch && sampleTick >= 0 && sampleTick <= _clockTick && (_clockTick - sampleTick) * _millisecondsPerTick <= maxAge)); return;
            case 0x0003: Put(MetaValue.Int(_nowMs)); return;
            case 0x0004:
                var message = GetString(Pop()); var errorCode = Pop();
                Put(CreateRecord(ResultType(), [errorCode, CreateString(TruncateUtf8(message, 512)), MetaValue.Int(0)])); return;
            case 0x0010: Put(CreateString(Pop().AsInt().ToString(CultureInfo.InvariantCulture))); return;
            case 0x0011: Put(CreateString(Pop().AsFloat().ToString("R", CultureInfo.InvariantCulture))); return;
            case 0x0012:
                var intText = GetString(Pop());
                Put(!IsDecimalInteger(intText) ? Error("InvalidArgument") : long.TryParse(intText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? Ok(MetaValue.Int(integer)) : Error("OutOfRange")); return;
            case 0x0013:
                var floatText = GetString(Pop());
                Put(!IsDecimalFloat(floatText) ? Error("InvalidArgument") : double.TryParse(floatText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? Ok(Float(number)) : Error("OutOfRange")); return;
            case 0x0014:
                var length = Pop().AsInt(); var start = Pop().AsInt(); var text = GetString(Pop());
                var scalarCount = text.EnumerateRunes().Count();
                if (start < 0 || length < 0 || start > scalarCount || length > scalarCount - start) { Put(Error("OutOfRange")); return; }
                var utf16Start = 0; var utf16Length = 0; var scalar = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (scalar < start) utf16Start += rune.Utf16SequenceLength;
                    else if (scalar < start + length) utf16Length += rune.Utf16SequenceLength;
                    else break;
                    scalar++;
                }
                Put(Ok(CreateString(text.Substring(utf16Start, utf16Length)))); return;
            case 0x0015: Put(MetaValue.Int(Encoding.UTF8.GetByteCount(GetString(Pop())))); return;
            case 0x0016: Put(MetaValue.Int(GetString(Pop()).EnumerateRunes().Count())); return;
            case 0x0017:
                var right = GetString(Pop()); var left = GetString(Pop());
                var concatenatedBytes = checked(Encoding.UTF8.GetByteCount(left) + Encoding.UTF8.GetByteCount(right));
                if (concatenatedBytes > _limits.StringBytes) Trap(MetaVmFaultCode.HeapLimit, "String byte limit.");
                Preflight(checked(160L + (left.Length + right.Length) * 2L + concatenatedBytes));
                Put(CreateString(string.Concat(left, right))); return;
            case 0x0020: Put(Float(Pop().AsInt())); return;
            case 0x0021:
                var floatValue = Pop().AsFloat();
                // 2^63 rounds exactly in binary64; the upper endpoint must remain exclusive.
                Put(floatValue >= long.MinValue && floatValue < 9223372036854775808.0 ? Ok(MetaValue.Int((long) Math.Truncate(floatValue))) : Error("OutOfRange")); return;
            case 0x0022: var byteValue = Pop().AsInt(); Put(byteValue is >= 0 and <= 255 ? Ok(MetaValue.Byte((byte) byteValue)) : Error("OutOfRange")); return;
            case 0x0023: var absoluteInt = Pop().AsInt(); if (absoluteInt == long.MinValue) Trap(MetaVmFaultCode.NumericFault, "absInt overflow."); Put(MetaValue.Int(Math.Abs(absoluteInt))); return;
            case 0x0024: Put(Float(Math.Abs(Pop().AsFloat()))); return;
            case 0x0025: Put(MetaValue.Int((byte) Pop().Payload)); return;
            case 0x0030:
                var rightSession = Pop(); var leftSession = Pop();
                Put(MetaValue.Bool(TryGetOpaqueToken(leftSession, out _) && TryGetOpaqueToken(rightSession, out _) && leftSession == rightSession)); return;
            case 0x0100:
                Put(Allocate(checked((int) typeId), new MetaHeapObject { Kind = MetaValueKind.List, Bytes = 32 })); return;
            case 0x0101: Put(MetaValue.Int(_heap.Get(Pop()).Count)); return;
            case 0x0102:
                var index = Pop().AsInt(); var list = _heap.Get(Pop()); Put(list.Values[CheckIndex(index, list.Count)]); return;
            case 0x0103:
                var replacement = Pop(); var replaceIndex = Pop().AsInt(); var replaced = _heap.Get(Pop()); replaced.Values[CheckIndex(replaceIndex, replaced.Count)] = replacement; return;
            case 0x0104:
                var appended = Pop(); var destination = _heap.Get(Pop()); CheckLength((long) destination.Count + 1);
                if (destination.Count == destination.Values.Length)
                {
                    var capacity = Math.Min(_limits.CollectionItems, Math.Max(4, checked(destination.Count * 2)));
                    _heap.Grow(destination, capacity, _limits.MemoryBytes - MemoryBytes, _limits.AllocationChurnBytes);
                }
                destination.Values[destination.Count++] = appended; destination.Version = checked(destination.Version + 1); return;
            case 0x0105:
                var popped = _heap.Get(Pop());
                if (popped.Count == 0) Put(CreateVariant(ResultType(), 0));
                else { var last = popped.Values[--popped.Count]; popped.Values[popped.Count] = default; popped.Version = checked(popped.Version + 1); Put(CreateVariant(ResultType(), 1, last)); }
                return;
            case 0x0106: Put(MetaValue.Int(_heap.Get(Pop()).Version)); return;
            case 0x0107:
                var expected = Pop().AsInt(); var checkedList = _heap.Get(Pop()); if (checkedList.Version != expected) Trap(MetaVmFaultCode.CollectionModified, "List changed during iteration."); return;
            case 0x0110:
                Put(Allocate(checked((int) typeId), new MetaHeapObject { Kind = MetaValueKind.Map, Entries = [], Bytes = 64 })); return;
            case 0x0111:
                var key = Pop(); var map = _heap.Get(Pop()); var found = FindMapKey(map, key);
                Put(found < 0 ? CreateVariant(ResultType(), 0) : CreateVariant(ResultType(), 1, map.Entries![found].Value)); return;
            case 0x0112:
                var mapValue = Pop(); var mapKey = Pop(); var changedMap = _heap.Get(Pop()); var mapIndex = FindMapKey(changedMap, mapKey);
                if (mapIndex < 0)
                {
                    CheckLength((long) changedMap.Count + 1);
                    var keyBytes = StringKeyBytes(mapKey);
                    if (keyBytes > _limits.MapStringKeyBytes - changedMap.KeyStringBytes) Trap(MetaVmFaultCode.HeapLimit, "Map string key byte limit.");
                    if (changedMap.Entries!.Count == changedMap.Entries.Capacity)
                    {
                        var capacity = Math.Min(_limits.CollectionItems, Math.Max(4, checked(changedMap.Count * 2)));
                        var bytes = checked(32L * capacity + 32);
                        _heap.ChargeMapGrowth(changedMap, bytes, _limits.MemoryBytes - MemoryBytes, _limits.AllocationChurnBytes);
                        changedMap.Entries.Capacity = capacity;
                    }
                    changedMap.Entries.Add(new KeyValuePair<MetaValue, MetaValue>(mapKey, mapValue)); changedMap.Count++; changedMap.KeyStringBytes += keyBytes;
                }
                else changedMap.Entries![mapIndex] = new KeyValuePair<MetaValue, MetaValue>(mapKey, mapValue);
                return;
            case 0x0113:
                var removedKey = Pop(); var removedMap = _heap.Get(Pop()); var removedIndex = FindMapKey(removedMap, removedKey);
                if (removedIndex >= 0) { removedMap.KeyStringBytes -= StringKeyBytes(removedMap.Entries![removedIndex].Key); removedMap.Entries.RemoveAt(removedIndex); removedMap.Count--; }
                Put(MetaValue.Bool(removedIndex >= 0)); return;
            case 0x0114:
                var keyed = _heap.Get(Pop()); Preflight(checked(160L + keyed.Count * 16L));
                Put(CreateArray(ResultType(), keyed.Entries!.Select(entry => entry.Key).ToArray())); return;
            default: Trap(MetaVmFaultCode.InvalidBytecode, "Unknown intrinsic."); return;
        }
    }

    private int FindMapKey(MetaHeapObject map, MetaValue key)
    {
        for (var index = 0; index < map.Entries!.Count; index++) if (Compare(map.Entries[index].Key, key) == 0) return index;
        return -1;
    }

    private int Cost(Frame frame, MetaInstruction instruction)
    {
        var op = instruction.Opcode;
        if (op is MetaOpcode.Call or MetaOpcode.CallAsync) return checked(4 + _module.Functions[instruction.A].ParameterTypes.Length);
        if (op is MetaOpcode.HostRead or MetaOpcode.HostAwait) return checked((op == MetaOpcode.HostRead ? 20 : 50) + _module.Imports[instruction.A].ParameterTypes.Length);
        if (op is MetaOpcode.RecordNew or MetaOpcode.RecordSet) return checked(20 + _module.Types[instruction.A].Fields.Length * 2);
        if (op == MetaOpcode.ArrayNew && frame.Count >= 2)
        {
            var length = frame.Stack[frame.Count - 2].AsInt();
            CheckLength(length); return checked(16 + (int) length);
        }
        if (op == MetaOpcode.ArrayLiteral) return checked(16 + (int) instruction.B);
        if (op is MetaOpcode.VariantNew) return 20;
        if (op is >= MetaOpcode.Eq and <= MetaOpcode.Ge && frame.Stack[frame.Count - 1] == frame.Stack[frame.Count - 2]) return 1;
        if (op is >= MetaOpcode.Eq and <= MetaOpcode.Ge && instruction.A == 5)
            return checked(4 + (Encoding.UTF8.GetByteCount(GetString(frame.Stack[frame.Count - 1])) + Encoding.UTF8.GetByteCount(GetString(frame.Stack[frame.Count - 2])) + 15) / 16);
        if (op is MetaOpcode.Eq or MetaOpcode.Ne && _module.Types[instruction.A].Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto or MetaTypeKind.Option or MetaTypeKind.Result)
            return checked(4 + (int) ((ComparisonBytes(frame.Stack[frame.Count - 1]) + ComparisonBytes(frame.Stack[frame.Count - 2]) + 15) / 16));
        if (op != MetaOpcode.CallIntrinsic) return 1;
        var signature = _artifact.Intrinsics[(instruction.A, instruction.B)];
        long bytes = 0;
        for (var parameter = 0; parameter < signature.Parameters.Length; parameter++)
        {
            var value = frame.Stack[frame.Count - signature.Parameters.Length + parameter];
            if (value.Kind == MetaValueKind.String) bytes += Encoding.UTF8.GetByteCount(GetString(value));
            else if (value.Kind == MetaValueKind.Map) { var map = _heap.Get(value); bytes += map.Count * 32L + map.KeyStringBytes; }
            else if (value.Kind == MetaValueKind.List && instruction.A is 0x0104 or 0x0105) bytes += _heap.Get(value).Count * 16L;
        }
        return checked(4 + (int) ((bytes + 15) / 16) + (signature.Effect == "Allocate" ? 16 : 0));
    }

    private long ComparisonBytes(MetaValue value)
    {
        try { return LogicalBytes(value, Math.Min(MaximumComparisonBytes, _limits.MemoryBytes), 0); }
        catch (MetaTrap trap) when (trap.Code == MetaVmFaultCode.HeapLimit && trap.Message == "Event payload limit.")
        { throw new MetaTrap(MetaVmFaultCode.HeapLimit, "Comparison work limit."); }
    }

    private static bool IsDecimalInteger(string value)
    {
        if (value.Length == 0) return false;
        var start = value[0] is '+' or '-' ? 1 : 0;
        return start != value.Length && value.AsSpan(start).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static bool IsDecimalFloat(string value)
    {
        // Anchored, nonbacktracking grammar; no locale, whitespace, NaN, Infinity or partial consumption.
        return Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]+)?)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(10));
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        var bytes = 0; var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > maximumBytes - bytes) break;
            bytes += rune.Utf8SequenceLength; length += rune.Utf16SequenceLength;
        }
        return length == value.Length ? value : value[..length];
    }
}
