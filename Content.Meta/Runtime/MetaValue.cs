// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Runtime.InteropServices;

namespace Content.Meta.Runtime;

public enum MetaValueKind : uint { Unit, Bool, Byte, Int, Float, Enum, String, Record, Variant, Array, List, Map, Capability, Opaque }

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
public readonly struct MetaValue : IEquatable<MetaValue>
{
    public readonly MetaValueKind Kind;
    public readonly uint TypeId;
    public readonly ulong Payload;

    internal MetaValue(MetaValueKind kind, int typeId, ulong payload) { Kind = kind; TypeId = checked((uint) typeId); Payload = payload; }
    public static MetaValue Unit => new(MetaValueKind.Unit, 0, 0);
    public static MetaValue Bool(bool value) => new(MetaValueKind.Bool, 1, value ? 1UL : 0UL);
    public static MetaValue Byte(byte value) => new(MetaValueKind.Byte, 2, value);
    public static MetaValue Int(long value) => new(MetaValueKind.Int, 3, unchecked((ulong) value));
    public static MetaValue Float(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return new MetaValue(MetaValueKind.Float, 4, BitConverter.DoubleToUInt64Bits(value == 0 ? 0 : value));
    }
    public long AsInt() => Kind == MetaValueKind.Int ? unchecked((long) Payload) : throw new InvalidOperationException("Value is not int.");
    public double AsFloat() => Kind == MetaValueKind.Float ? BitConverter.UInt64BitsToDouble(Payload) : throw new InvalidOperationException("Value is not float.");
    public bool AsBool() => Kind == MetaValueKind.Bool ? Payload != 0 : throw new InvalidOperationException("Value is not bool.");
    public bool Equals(MetaValue other) => Kind == other.Kind && TypeId == other.TypeId && Payload == other.Payload;
    public override bool Equals(object? obj) => obj is MetaValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, TypeId, Payload);
    public static bool operator ==(MetaValue left, MetaValue right) => left.Equals(right);
    public static bool operator !=(MetaValue left, MetaValue right) => !left.Equals(right);
}

public enum MetaVmFaultCode : ushort { GasLimit, StackLimit, HeapLimit, BoundsFault, NumericFault, InvalidBytecode, CollectionModified, InternalFault }
public enum MetaProcessState { Idle, Runnable, Running, Waiting, Collecting, Stopped, Faulted, Quarantined }
public enum MetaStopReason { None, Completed, Stop, PowerLost, PermissionRevoked, EventOverflow, Runaway, GcWorkExceeded, ArtifactRevoked, InternalFault, DebugPauseTimeout }
public sealed record MetaFault(MetaVmFaultCode Code, string Detail);
public sealed record MetaSliceResult(MetaProcessState State, int GasUsed, int Instructions, MetaFault? Fault, MetaStopReason StopReason);
public sealed record MetaProcessLimits
{
    public long MemoryBytes { get; init; } = 1024 * 1024;
    public int Frames { get; init; } = 64;
    public int StackSlots { get; init; } = 16384;
    public long ActivationGas { get; init; } = 1_000_000;
    public long ActivationMilliseconds { get; init; } = 30_000;
    public long AllocationChurnBytes { get; init; } = 16 * 1024 * 1024;
    public int StringBytes { get; init; } = 65536;
    public int CollectionItems { get; init; } = 65536;
    public int MapStringKeyBytes { get; init; } = 65536;
    public int Events { get; init; } = 64;
    public long EventBytes { get; init; } = 65536;
    public int GcWork { get; init; } = 1_000_000;
    public int LogBytes { get; init; } = 4096;
    public bool RetainForInvocations { get; init; }
}

public sealed record MetaHostRequest(int ImportId, MetaValue Capability, IReadOnlyList<MetaValue> Arguments);
public readonly record struct MetaHostResult(bool Pending, long OperationId, MetaValue Value)
{
    public static MetaHostResult Completed(MetaValue value) => new(false, 0, value);
    public static MetaHostResult Awaiting(long operationId) => new(true, operationId, default);
}

/// <summary>All methods are bounded, synchronous admission/read paths. Delayed work completes through TryCompleteHost.</summary>
public interface IMetaHost
{
    MetaValue Read(MetaProcess process, MetaHostRequest request);
    MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request);
    void Log(MetaProcess process, string text);
    void Cancel(MetaProcess process, long operationId);
}

public sealed class MetaNullHost : IMetaHost
{
    public static MetaNullHost Instance { get; } = new();
    private MetaNullHost() { }
    public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("No host bound.");
    public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("No host bound.");
    public void Log(MetaProcess process, string text) { }
    public void Cancel(MetaProcess process, long operationId) { }
}

internal sealed class MetaTrap(MetaVmFaultCode code, string detail) : Exception(detail)
{
    public MetaVmFaultCode Code { get; } = code;
}
