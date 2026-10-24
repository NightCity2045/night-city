// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Content.Meta.Diagnostics;

namespace Content.Server._NC.Meta.Diagnostics;

public sealed record NCMetaEngineTickCoverage(long Started, long Completed, long MissingPairs, long BoundaryPartials,
    bool ProviderObserved, string Coverage);

/// <summary>Pairs the existing public Robust.GameLoop EventSource callbacks; never changes the engine or its tick cadence.</summary>
internal sealed class NCMetaEngineTickListener : EventListener
{
    private readonly MetaTickPairer _pairs = new();
    private IMetaMetrics? _sink;
    private bool _firstStopIsBoundary;
    private bool _providerObserved;
    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name != "Robust.GameLoop") return;
        _providerObserved = true; EnableEvents(source, EventLevel.Informational, EventKeywords.All);
    }
    public void SetSink(IMetaMetrics? sink)
    {
        _pairs.Reset(); _sink = sink; _firstStopIsBoundary = true;
    }
    public NCMetaEngineTickCoverage GetCoverage() => new(_pairs.Started, _pairs.Completed, _pairs.MissingPairs,
        _pairs.BoundaryPartials, _providerObserved, "complete-engine-Tick-callback-elapsed;partial-capture-edges-excluded;not-CPU;paired-callback-overhead-recorded");
    protected override void OnEventWritten(EventWrittenEventArgs args)
    {
        if (args.EventId is not (4 or 5) || !MetaMetrics.IsEnabled(_sink)) return;
        var callback = Stopwatch.GetTimestamp();
        try
        {
            if (args.Payload is not { Count: 1 }) return;
            var tick = Convert.ToUInt32(args.Payload[0], System.Globalization.CultureInfo.InvariantCulture);
            var thread = Environment.CurrentManagedThreadId;
            if (args.EventId == 4) { _firstStopIsBoundary = false; _pairs.Begin(tick, thread, callback); }
            else if (_firstStopIsBoundary) _firstStopIsBoundary = false;
            else if (_pairs.TryEnd(tick, thread, callback, out var elapsed)) MetaMetrics.Record(_sink, MetaMetric.EngineTickElapsed, elapsed, tick);
            MetaMetrics.Latency(_sink, MetaMetric.EngineListenerElapsed, callback, tick);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { _pairs.DiscardBoundary(); }
    }
    public override void Dispose()
    {
        _pairs.DiscardBoundary(); _sink = null; base.Dispose();
    }
}
