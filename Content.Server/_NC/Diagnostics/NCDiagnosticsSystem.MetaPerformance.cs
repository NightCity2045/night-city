// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Text.Json;
using System.Text.Json.Nodes;
using Content.Meta.Diagnostics;
using Content.Server._NC.Meta.Diagnostics;
using NightCity.Diagnostics.Contracts;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    [Dependency] private NCMetaPerformanceSystem _metaPerformance = default!;
    private JsonObject StartMetaPerformance(JsonObject args)
    {
        RequireFixtureAdmin();
        var id = _metaPerformance.TryStart(new(args["workload"]?.GetValue<string>() ?? "A", args["mode"]?.GetValue<string>() ?? "Normal",
            args["seed"]?.GetValue<int>() ?? 90210, args.BoundedInt("warmupSeconds", 30, 0, 120), args.BoundedInt("captureSeconds", 60, 1, 60),
            args["baseline"]?.GetValue<bool>() ?? false, args["sourceHash"]?.GetValue<string>() ?? "", args["resourceHash"]?.GetValue<string>() ?? ""));
        if (id == Guid.Empty) throw new DiagnosticException("busy", "The isolated META workload was not admitted; inspect existing capture or world readiness.", true);
        return new JsonObject { ["captureId"] = id.ToString("N"), ["stage"] = "Preparing", ["isolated"] = true };
    }
    private Guid PerformanceId(JsonObject args) => Guid.TryParseExact(args.RequiredString("captureId", 32), "N", out var id)
        ? id : throw new DiagnosticException("invalid_arguments", "Expected a diagnostic capture identifier.");
    private JsonObject StopMetaPerformance(JsonObject args)
    {
        RequireFixtureAdmin(); var id = PerformanceId(args);
        if (!_metaPerformance.TryStop(id, args["cleanup"]?.GetValue<bool>() ?? false)) throw new DiagnosticException("capture_not_found", "The META capture is unavailable.");
        return new JsonObject { ["stopped"] = true, ["captureId"] = id.ToString("N") };
    }
    private JsonObject ReadMetaPerformance(JsonObject args, bool samples)
    {
        var id = PerformanceId(args);
        if (!samples)
        {
            var status = _metaPerformance.GetStatus(id) ?? throw new DiagnosticException("capture_not_found", "The META capture is unavailable.");
            return new JsonObject { ["run"] = JsonSerializer.SerializeToNode(status, DiagnosticProtocol.JsonOptions),
                ["metricLabels"] = JsonSerializer.SerializeToNode(Enum.GetNames<MetaMetric>(), DiagnosticProtocol.JsonOptions),
                ["timingCoverage"] = "elapsed-stopwatch-ticks-not-thread-cpu", ["schemaVersion"] = 1 };
        }
        var page = _metaPerformance.ReadSamples(id, args.BoundedInt("offset", 0, 0, MetaMetricCapture.MaximumSamples), args.BoundedInt("count", 512, 1, 512));
        if (page == null) throw new DiagnosticException("capture_not_ready", "Stop the capture before reading detached raw pages.", true);
        return new JsonObject { ["page"] = JsonSerializer.SerializeToNode(page, DiagnosticProtocol.JsonOptions) };
    }
}
#endif
