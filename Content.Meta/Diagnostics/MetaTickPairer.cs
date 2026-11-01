// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Diagnostics;

/// <summary>Single game-thread event pairing. Missing/mismatched stops never become fabricated zero-duration ticks.</summary>
public sealed class MetaTickPairer
{
    private bool _pending;
    private uint _tick;
    private int _thread;
    private long _started;
    public long Started { get; private set; }
    public long Completed { get; private set; }
    public long MissingPairs { get; private set; }
    public long BoundaryPartials { get; private set; }
    public void Reset()
    {
        _pending = false; _tick = 0; _thread = 0; _started = 0;
        Started = Completed = MissingPairs = BoundaryPartials = 0;
    }
    public void Begin(uint tick, int thread, long timestamp)
    {
        if (_pending) MissingPairs++;
        _pending = true; _tick = tick; _thread = thread; _started = timestamp; Started++;
    }
    public bool TryEnd(uint tick, int thread, long timestamp, out long elapsed)
    {
        elapsed = 0;
        var valid = _pending && _tick == tick && _thread == thread && timestamp >= _started;
        _pending = false;
        if (!valid) { MissingPairs++; return false; }
        Completed++; elapsed = timestamp - _started; return true;
    }
    public void DiscardBoundary()
    {
        if (_pending) BoundaryPartials++;
        _pending = false;
    }
}
