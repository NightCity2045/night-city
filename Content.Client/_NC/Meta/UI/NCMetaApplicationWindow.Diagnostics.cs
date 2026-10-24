// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Diagnostics;
using System.Linq;
using System.Text;
using Content.Meta.Mui;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

public readonly record struct NCMetaFrontendSample(long Timestamp, string Kind, Guid RequestId, long Revision, long LatencyTicks);
public sealed record NCMetaFrontendSnapshot(Guid SessionId, long Revision, long AcknowledgedRevision, Guid LastAction,
    Guid LastCompletedAction, bool LastActionSuccess, int Samples, int DroppedSamples, long Frequency,
    long FullBytes, long PatchBytes, int NativeControls, bool Recording);

public sealed partial class NCMetaApplicationWindow
{
    private NCMetaFrontendSample[]? _diagnosticSamples;
    private readonly Dictionary<Guid, long> _diagnosticPending = [];
    private int _diagnosticCount;
    private int _diagnosticDropped;
    private bool _diagnosticRecording;
    private long _diagnosticAck = -1;
    private long _diagnosticFullBytes;
    private long _diagnosticPatchBytes;
    private Guid _diagnosticLastAction;
    private Guid _diagnosticLastCompleted;
    private bool _diagnosticLastSuccess;

    public bool TryStartDiagnosticCapture()
    {
        if (_renderer == null || SessionId == Guid.Empty || _diagnosticRecording) return false;
        _diagnosticSamples = new NCMetaFrontendSample[2048]; _diagnosticCount = _diagnosticDropped = 0;
        _diagnosticFullBytes = _diagnosticPatchBytes = 0; _diagnosticPending.Clear();
        _diagnosticLastAction = _diagnosticLastCompleted = Guid.Empty; _diagnosticLastSuccess = false;
        _diagnosticRecording = true;
        return true;
    }
    public void StopDiagnosticCapture() => _diagnosticRecording = false;
    public NCMetaFrontendSnapshot GetDiagnosticSnapshot() => new(SessionId, _renderer?.Revision ?? -1, _diagnosticAck,
        _diagnosticLastAction, _diagnosticLastCompleted, _diagnosticLastSuccess, _diagnosticCount, _diagnosticDropped,
        Stopwatch.Frequency, _diagnosticFullBytes, _diagnosticPatchBytes, _renderer?.NativeControlCount ?? 0, _diagnosticRecording);
    public NCMetaFrontendSample[] ReadDiagnosticSamples(int offset, int count) =>
        _diagnosticSamples == null || offset < 0 || count is < 1 or > 512 || offset > _diagnosticCount ? [] :
            _diagnosticSamples.Skip(offset).Take(Math.Min(count, _diagnosticCount - offset)).ToArray();
    public bool TryRequestDiagnosticAction(string action, string json, out Guid requestId)
    {
        requestId = Guid.Empty;
        if (!_diagnosticRecording || _renderer == null || _diagnosticPending.Count != 0 ||
            !_renderer.TryRequestAction(action, json)) return false;
        requestId = _diagnosticLastAction;
        return requestId != Guid.Empty;
    }
    private void RecordDiagnosticAction(Guid id)
    {
        if (!_diagnosticRecording) return;
        _diagnosticLastAction = id;
        if (_diagnosticPending.Count < 16) _diagnosticPending[id] = Stopwatch.GetTimestamp();
        AppendDiagnostic("action-sent", id, _renderer?.Revision ?? -1);
    }
    private void RecordDiagnosticResult(NCMetaAppActionResultMessage result)
    {
        if (!_diagnosticRecording || result.SessionId != SessionId) return;
        var latency = _diagnosticPending.TryGetValue(result.RequestId, out var started) ? Stopwatch.GetTimestamp() - started : 0;
        AppendDiagnostic(result.Pending ? "action-pending" : "action-completed", result.RequestId, result.Revision, latency);
        if (result.Pending) return;
        _diagnosticPending.Remove(result.RequestId); _diagnosticLastCompleted = result.RequestId; _diagnosticLastSuccess = result.Success;
    }
    private void RecordDiagnosticUpdate(NCMetaAppState state, MuiUpdateResult result)
    {
        if (result is MuiUpdateResult.Applied or MuiUpdateResult.Duplicate) _diagnosticAck = _renderer?.Revision ?? -1;
        if (!_diagnosticRecording) return;
        var bytes = Encoding.UTF8.GetByteCount(state.StateJson);
        if (state.FullSnapshot) _diagnosticFullBytes += bytes; else _diagnosticPatchBytes += bytes;
        AppendDiagnostic(result switch
        { MuiUpdateResult.Applied => "state-applied-ack-sent", MuiUpdateResult.Duplicate => "state-duplicate-ack-sent", MuiUpdateResult.Gap => "state-gap", _ => "state-rejected" }, Guid.Empty, state.Revision);
    }
    private void AppendDiagnostic(string kind, Guid request, long revision, long latency = 0)
    {
        if (_diagnosticSamples == null) return;
        if (_diagnosticCount >= _diagnosticSamples.Length) { _diagnosticDropped++; return; }
        _diagnosticSamples[_diagnosticCount++] = new(Stopwatch.GetTimestamp(), kind, request, revision, latency);
    }
}
#endif
