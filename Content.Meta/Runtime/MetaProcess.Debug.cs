// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using Content.Meta.Bytecode;

namespace Content.Meta.Runtime;

public enum MetaDebugStepKind { None, Into, Over, Out }

public sealed partial class MetaProcess
{
    private readonly HashSet<(int Function, int Offset)> _breakpoints = [];
    private (int Function, int Offset)? _skipBreakpoint;
    private MetaDebugStepKind _debugStep;
    private int _debugStepDepth;
    private long _debugPausedAt;
    public bool DebugPaused { get; private set; }
    public long? DebugPauseDeadlineMs => DebugPaused ? _debugPausedAt > long.MaxValue - 10000 ? long.MaxValue : _debugPausedAt + 10000 : null;

    public bool TrySetBreakpoint(int functionId, int pcByteOffset, bool enabled = true)
    {
        if (functionId < 0 || functionId >= _artifact.InstructionIndices.Length || !_artifact.InstructionIndices[functionId].ContainsKey(pcByteOffset)) return false;
        if (!enabled) return _breakpoints.Remove((functionId, pcByteOffset));
        return _breakpoints.Count < 128 && _breakpoints.Add((functionId, pcByteOffset));
    }

    public bool TryPause(long nowMs)
    {
        if (_running || nowMs < 0 || _frames.Count == 0 || State is not (MetaProcessState.Runnable or MetaProcessState.Waiting or MetaProcessState.Collecting)) return false;
        PauseDebug(nowMs);
        return true;
    }

    public bool TryContinue()
    {
        if (_running || !DebugPaused || State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) return false;
        _skipBreakpoint = CurrentLocation(); _debugStep = MetaDebugStepKind.None; DebugPaused = false;
        return true;
    }
    public bool TryStepInto() => TryStep(MetaDebugStepKind.Into);
    public bool TryStepOver() => TryStep(MetaDebugStepKind.Over);
    public bool TryStepOut() => TryStep(MetaDebugStepKind.Out);
    private bool TryStep(MetaDebugStepKind kind)
    {
        if (_frames.Count == 0 || !TryContinue()) return false;
        _debugStep = kind; _debugStepDepth = _frames.Count;
        return true;
    }

    public MetaDebugSpan? GetSourceSpan(int functionId, int pcByteOffset)
    {
        MetaDebugSpan? best = null;
        if (_module.Debug == null) return null;
        foreach (var span in _module.Debug.Spans)
            if (span.FunctionId == functionId && span.PcOffset <= pcByteOffset && (best == null || span.PcOffset > best.PcOffset)) best = span;
        return best;
    }

    private (int Function, int Offset)? CurrentLocation()
    {
        if (_frames.Count == 0) return null;
        var frame = _frames[^1]; var instructions = _artifact.Instructions[frame.Function.Id];
        return frame.Pc < instructions.Length ? (frame.Function.Id, instructions[frame.Pc].Offset) : null;
    }
    private bool BreakBeforeInstruction(Frame frame, MetaInstruction instruction)
    {
        var location = (frame.Function.Id, instruction.Offset);
        if (!_breakpoints.Contains(location) || _skipBreakpoint == location) return false;
        PauseDebug(_nowMs); State = MetaProcessState.Runnable;
        return true;
    }
    private void BreakAfterInstruction()
    {
        _skipBreakpoint = null;
        if (_debugStep == MetaDebugStepKind.None) return;
        var shouldPause = _debugStep switch
        {
            MetaDebugStepKind.Into => true,
            MetaDebugStepKind.Over => _frames.Count <= _debugStepDepth,
            MetaDebugStepKind.Out => _frames.Count < _debugStepDepth,
            _ => false
        };
        if (!shouldPause) return;
        _debugStep = MetaDebugStepKind.None;
        if (_frames.Count == 0) return;
        PauseDebug(_nowMs);
        if (State == MetaProcessState.Running) State = MetaProcessState.Runnable;
    }
    private void PauseDebug(long nowMs) { DebugPaused = true; _debugPausedAt = nowMs; }

    private MetaDebugValue DescribeValue(MetaValue value)
    {
        var display = value.Kind switch
        {
            MetaValueKind.Unit => "()",
            MetaValueKind.Bool => value.Payload == 0 ? "false" : "true",
            MetaValueKind.Byte => value.Payload.ToString(CultureInfo.InvariantCulture),
            MetaValueKind.Int => value.AsInt().ToString(CultureInfo.InvariantCulture),
            MetaValueKind.Float => value.AsFloat().ToString("R", CultureInfo.InvariantCulture),
            MetaValueKind.Enum => GetVariantDefinition((int) value.TypeId, (int) value.Payload).Name,
            MetaValueKind.String => TruncateUtf8(GetString(value), 128),
            MetaValueKind.Capability => "<capability>",
            MetaValueKind.Opaque => "<opaque>",
            MetaValueKind.Record => "{" + _heap.Get(value).Count.ToString(CultureInfo.InvariantCulture) + " fields}",
            MetaValueKind.Variant => GetVariantDefinition((int) value.TypeId, _heap.Get(value).Tag).Name,
            _ => "[" + _heap.Get(value).Count.ToString(CultureInfo.InvariantCulture) + " items]"
        };
        return new MetaDebugValue(value.TypeId, value.Kind, display);
    }
}

public sealed record MetaDebugValue(uint TypeId, MetaValueKind Kind, string Display);
