// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;

namespace Content.Meta.Testing;

public sealed record MetaTestResponse(string Member, JsonElement Value, string? ErrorCode = null,
    string ErrorMessage = "", bool Pending = false, int CompleteAfterSlices = 1);
public sealed record MetaTestEvent(string Slot, string Event, JsonElement[] Arguments, long Sequence,
    long NowMs = 1000, long Tick = 60);
public sealed record MetaTestFixture(string RootModule, MetaTestResponse[] Responses, MetaTestEvent[] Events,
    string[]? ExpectedLogs = null, string[]? ExpectedCommands = null, long Epoch = 1, long NowMs = 1000, long Tick = 60);
public sealed record MetaTestSuite(string Version, MetaTestFixture[] Programs);
public sealed record MetaTestLimits(int MaxSlices = 4096, int GasPerSlice = 512, long MaxGas = 200000,
    int MaxMilliseconds = 500, long HeapBytes = 1024 * 1024);
public enum MetaTestStatus { Passed, Failed, Faulted, LimitExceeded, InvalidFixture }
public sealed record MetaTestCommand(string Member, string[] Arguments)
{
    public string Trace => Member[(Member.LastIndexOf('.') + 1)..] + "(" + string.Join(',', Arguments) + ")";
}
public sealed record MetaTestRunResult(MetaTestStatus Status, string[] Logs, MetaTestCommand[] Commands,
    string? FailureCode, MetaFault? Fault, long GasUsed, long MemoryBytes, int Slices)
{
    public bool Passed => Status == MetaTestStatus.Passed;
}

/// <summary>Production test execution with a process-local fake ABI host. It never calls an ECS adapter or grants live authority.</summary>
public static class MetaTestHost
{
    public const int MaximumFixtureBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static MetaTestFixture ParseFixture(string json)
    {
        using var document = ParseDocument(json);
        var fixture = JsonSerializer.Deserialize<MetaTestFixture>(document.RootElement, JsonOptions);
        if (!HasValidShape(fixture)) throw new ArgumentException("Invalid META fixture structure.");
        return fixture!;
    }
    public static MetaTestSuite ParseSuite(string json)
    {
        using var document = ParseDocument(json);
        var suite = JsonSerializer.Deserialize<MetaTestSuite>(document.RootElement, JsonOptions) ?? throw new ArgumentException("Empty META suite.");
        if (suite.Version != "1.0" || suite.Programs is null || suite.Programs.Length > 64 || suite.Programs.Any(f => !HasValidShape(f)))
            throw new ArgumentException("Unsupported META fixture suite.");
        return suite;
    }

    private static JsonDocument ParseDocument(string json)
    {
        if (json is null || json.Length > MaximumFixtureBytes || Encoding.UTF8.GetByteCount(json) > MaximumFixtureBytes)
            throw new ArgumentException("META fixture byte limit.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            var nodes = 0;
            Validate(document.RootElement);
            return document;
            void Validate(JsonElement element)
            {
                if (++nodes > 8192) throw new ArgumentException("META fixture node limit.");
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new JsonException("Duplicate META fixture property.");
                        Validate(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var item in element.EnumerateArray()) Validate(item);
            }
        }
        catch { document.Dispose(); throw; }
    }

    private static bool HasValidShape(MetaTestFixture? fixture)
    {
        if (fixture is null || string.IsNullOrEmpty(fixture.RootModule) || fixture.RootModule.Length > 128 ||
            fixture.Responses is null || fixture.Responses.Length > 64 || fixture.Events is null || fixture.Events.Length > 64 ||
            fixture.Epoch < 0 || fixture.Tick < 0 || fixture.NowMs < 0 || !ValidExpectations(fixture.ExpectedLogs) || !ValidExpectations(fixture.ExpectedCommands)) return false;
        foreach (var response in fixture.Responses)
            if (response is null || string.IsNullOrEmpty(response.Member) || response.Member.Length > 256 || response.ErrorMessage is null ||
                response.ErrorMessage.Length > 512 || response.CompleteAfterSlices is < 1 or > 64 ||
                response.ErrorCode is null && response.Value.ValueKind == JsonValueKind.Undefined || response.ErrorCode?.Length > 64) return false;
        foreach (var value in fixture.Events)
            if (value is null || string.IsNullOrEmpty(value.Slot) || value.Slot.Length > 128 || string.IsNullOrEmpty(value.Event) || value.Event.Length > 128 ||
                value.Arguments is null || value.Arguments.Length > 8 || value.Sequence < 1 || value.NowMs < 0 || value.Tick < 0) return false;
        return true;
        static bool ValidExpectations(string[]? values) => values is null || values.Length <= 64 && values.All(v => v is not null && v.Length <= 4096);
    }

    public static MetaTestRunResult Run(VerifiedArtifact artifact, MetaTestFixture fixture, MetaTestLimits? limits = null,
        CancellationToken cancellation = default)
    {
        limits ??= new();
        if (limits.MaxSlices is < 1 or > 4096 || limits.GasPerSlice is < 1 or > 4096 || limits.MaxGas is < 1 or > 1000000 ||
            limits.MaxMilliseconds is < 1 or > 1000 || limits.HeapBytes is < 4096 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var host = new FixtureHost(artifact, fixture);
        MetaProcess? process = null; var slices = 0; long gas = 0; var clock = Stopwatch.StartNew();
        MetaTestRunResult Result(MetaTestStatus status, string? code = null) => new(status, host.Logs.ToArray(), host.Commands.ToArray(), code,
            status == MetaTestStatus.InvalidFixture ? null : process?.Fault, gas, process?.MemoryBytes ?? 0, slices);
        try
        {
            host.Validate();
            process = new MetaProcess(artifact, host, new MetaProcessLimits { MemoryBytes = limits.HeapBytes, ActivationGas = limits.MaxGas, ActivationMilliseconds = 30000 });
            process.SetClock(fixture.Epoch, fixture.Tick, 1000.0 / 60);
            for (var id = 0; id < artifact.GetLinks().Length; id++)
                if (!process.TryBindLink(id, (ulong) id + 1)) throw new InvalidFixtureException("meta-test-link-binding");
            if (!process.TryStartMain()) throw new InvalidFixtureException("meta-test-main-rejected");
            var eventIndex = 0; var now = fixture.NowMs;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++slices > limits.MaxSlices || gas >= limits.MaxGas || clock.ElapsedMilliseconds > limits.MaxMilliseconds)
                    return Result(MetaTestStatus.LimitExceeded, "meta-test-resource-limit");
                if (process.State is MetaProcessState.Runnable or MetaProcessState.Collecting)
                {
                    var slice = process.RunSlice(limits.GasPerSlice, now, 512, TimeSpan.FromMilliseconds(5)); gas += slice.GasUsed; continue;
                }
                if (process.State == MetaProcessState.Waiting)
                {
                    if (!host.CompletePending(process)) throw new InvalidFixtureException("meta-test-unexpected-wait");
                    continue;
                }
                if (host.FixtureFailure is { } failure) return Result(MetaTestStatus.InvalidFixture, failure);
                if (process.Fault != null) return Result(MetaTestStatus.Faulted, "meta-test-vm-fault");
                if (eventIndex < fixture.Events.Length)
                {
                    if (process.State != MetaProcessState.Idle) throw new InvalidFixtureException("meta-test-event-process-stopped");
                    var value = fixture.Events[eventIndex++]; now = value.NowMs;
                    process.SetClock(fixture.Epoch, value.Tick, 1000.0 / 60);
                    var watch = host.ResolveWatch(value.Slot, value.Event);
                    var contract = artifact.GetImport(artifact.GetWatches()[watch].EventImportId);
                    if (contract.ParameterTypes.Length != value.Arguments.Length) throw new InvalidFixtureException("meta-test-event-arguments");
                    var arguments = value.Arguments.Select((v, i) => host.Decode(process, contract.ParameterTypes[i], v)).ToArray();
                    if (!process.TryEnqueueEvent(watch, arguments, value.Sequence) || !process.TryStartNextEvent()) throw new InvalidFixtureException("meta-test-event-rejected");
                    continue;
                }
                if (host.ResponsesUsed != fixture.Responses.Length) return Result(MetaTestStatus.Failed, "meta-test-unused-responses");
                if (fixture.ExpectedLogs != null && !host.Logs.SequenceEqual(fixture.ExpectedLogs, StringComparer.Ordinal)) return Result(MetaTestStatus.Failed, "meta-test-log-mismatch");
                if (fixture.ExpectedCommands != null && !host.Commands.Select(c => c.Trace).SequenceEqual(fixture.ExpectedCommands, StringComparer.Ordinal)) return Result(MetaTestStatus.Failed, "meta-test-command-mismatch");
                return Result(MetaTestStatus.Passed);
            }
        }
        catch (InvalidFixtureException exception) { return Result(MetaTestStatus.InvalidFixture, exception.Message); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or OverflowException)
        { return Result(MetaTestStatus.InvalidFixture, "meta-test-invalid-value"); }
        finally { process?.Stop(MetaStopReason.Stop); }
    }

    private sealed class FixtureHost(VerifiedArtifact artifact, MetaTestFixture fixture) : IMetaHost
    {
        private readonly Dictionary<(int Type, string Name), ulong> _opaque = new();
        private readonly Dictionary<ulong, string> _opaqueNames = new();
        private (long Id, MetaValue Value, int Remaining)? _pending;
        public List<string> Logs { get; } = new();
        public List<MetaTestCommand> Commands { get; } = new();
        public int ResponsesUsed { get; private set; }
        public string? FixtureFailure { get; private set; }
        private int _valueNodes;
        private int _traceBytes;
        public void Validate()
        {
            if (!HasValidShape(fixture) || artifact.Manifest.RootModule != fixture.RootModule || fixture.Responses.Length > 64 || fixture.Events.Length > 64 ||
                fixture.Epoch < 0 || fixture.Tick < 0 || fixture.NowMs < 0 || fixture.ExpectedLogs?.Length > 64 || fixture.ExpectedCommands?.Length > 64)
                throw new InvalidFixtureException("meta-test-fixture-limit");
            foreach (var response in fixture.Responses)
                if (!MetaApiCatalog.Default.Members.Any(m => m.QualifiedName == response.Member && m.Effect != "Event") || response.CompleteAfterSlices is < 1 or > 64 || response.ErrorMessage.Length > 512 ||
                    response.ErrorCode is { } error && !MetaApiCatalog.Default.Types.Single(t => t.Name == "meta.system.ErrorCode").Variants.Contains(error, StringComparer.Ordinal))
                    throw new InvalidFixtureException("meta-test-unknown-member");
            foreach (var value in fixture.Events)
                if (value.Sequence < 1 || value.NowMs < 0 || value.Tick < 0 || value.Arguments.Length > 8 || value.Slot.Length > 128 || value.Event.Length > 128)
                    throw new InvalidFixtureException("meta-test-event-limit");
        }
        public int ResolveWatch(string slot, string eventName)
        {
            var links = artifact.GetLinks(); var watches = artifact.GetWatches();
            var matches = Enumerable.Range(0, watches.Length).Where(i => links[watches[i].LinkId].SlotName == slot && artifact.GetImport(watches[i].EventImportId).Name.EndsWith("." + eventName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new InvalidFixtureException("meta-test-watch-not-found"); return matches[0];
        }
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => Respond(process, request, false).Value;
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => Respond(process, request, true);
        private MetaHostResult Respond(MetaProcess process, MetaHostRequest request, bool async)
        {
            try { return RespondCore(process, request, async); }
            catch (InvalidFixtureException exception)
            {
                FixtureFailure = exception.Message;
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or OverflowException)
            {
                FixtureFailure = "meta-test-invalid-value";
                throw;
            }
        }
        private MetaHostResult RespondCore(MetaProcess process, MetaHostRequest request, bool async)
        {
            var import = artifact.GetImport(request.ImportId);
            if (!process.TryGetHostToken(request.Capability, out _) || !MetaApiCatalog.Default.Members.Any(m => m.QualifiedName == import.Name && m.Effect == (async ? "Await" : "Read")))
                throw new InvalidFixtureException("meta-test-host-whitelist");
            var command = new MetaTestCommand(import.Name, request.Arguments.Select(a => Display(process, a)).ToArray());
            Trace(command.Trace); Commands.Add(command);
            if (ResponsesUsed >= fixture.Responses.Length) throw new InvalidFixtureException("meta-test-missing-response");
            var response = fixture.Responses[ResponsesUsed++];
            if (response.Member != import.Name) throw new InvalidFixtureException("meta-test-response-order");
            var result = response.ErrorCode != null ? process.CreateErrorResult(import.ReturnType!.Value, response.ErrorCode, response.ErrorMessage) :
                process.CreateResult(import.ReturnType!.Value, Decode(process, artifact.GetType(import.ReturnType.Value).Args[0], response.Value));
            if (!response.Pending) return MetaHostResult.Completed(result);
            if (!async || _pending != null) throw new InvalidFixtureException("meta-test-pending-limit");
            var operation = ResponsesUsed + 1L; _pending = (operation, result, response.CompleteAfterSlices); return MetaHostResult.Awaiting(operation);
        }
        public bool CompletePending(MetaProcess process)
        {
            if (_pending is not { } pending) return false;
            if (pending.Remaining > 1) { _pending = pending with { Remaining = pending.Remaining - 1 }; return true; }
            _pending = null; return process.TryCompleteHost(pending.Id, pending.Value);
        }
        public void Log(MetaProcess process, string text) { Trace(text); if (Logs.Count >= 64) throw new InvalidFixtureException("meta-test-log-limit"); Logs.Add(text); }
        public void Cancel(MetaProcess process, long operationId) => _pending = null;
        private void Trace(string value)
        {
            if ((_traceBytes += Encoding.UTF8.GetByteCount(value)) > 64 * 1024 || Commands.Count >= 64) throw new InvalidFixtureException("meta-test-trace-limit");
        }
        private string Display(MetaProcess process, MetaValue value) => value.Kind switch
        {
            MetaValueKind.Unit => "()", MetaValueKind.Bool => value.AsBool() ? "true" : "false", MetaValueKind.Byte => value.Payload.ToString(CultureInfo.InvariantCulture),
            MetaValueKind.Int => value.AsInt().ToString(CultureInfo.InvariantCulture), MetaValueKind.Float => value.AsFloat().ToString("R", CultureInfo.InvariantCulture),
            MetaValueKind.String => process.GetString(value), MetaValueKind.Enum => artifact.GetType((int) value.TypeId).Variants.Single(v => v.Tag == process.GetVariantTag(value)).Name,
            MetaValueKind.Opaque when process.TryGetOpaqueToken(value, out var token) && _opaqueNames.TryGetValue(token, out var name) => name,
            _ => throw new InvalidFixtureException("meta-test-command-value")
        };

        public MetaValue Decode(MetaProcess process, int typeId, JsonElement value, int depth = 0)
        {
            if (++_valueNodes > 8192 || depth > 16) throw new InvalidFixtureException("meta-test-value-limit");
            var type = artifact.GetType(typeId);
            switch (type.Kind)
            {
                case MetaTypeKind.Unit:
                    if (value.ValueKind != JsonValueKind.Null) throw new InvalidFixtureException("meta-test-unit-value"); return MetaValue.Unit;
                case MetaTypeKind.Bool: return MetaValue.Bool(value.GetBoolean());
                case MetaTypeKind.Byte: return MetaValue.Byte(value.GetByte());
                case MetaTypeKind.Int: return MetaValue.Int(value.GetInt64());
                case MetaTypeKind.Float: return MetaValue.Float(value.GetDouble());
                case MetaTypeKind.String:
                    var text = value.GetString() ?? throw new InvalidFixtureException("meta-test-string-value");
                    if (Encoding.UTF8.GetByteCount(text) > 32768) throw new InvalidFixtureException("meta-test-string-limit"); return process.CreateString(text);
                case MetaTypeKind.Enum:
                    var variant = type.Variants.SingleOrDefault(v => v.Name == value.GetString()) ?? throw new InvalidFixtureException("meta-test-enum-value");
                    return process.CreateEnum(typeId, variant.Tag);
                case MetaTypeKind.Struct or MetaTypeKind.NativeDto:
                    if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != type.Fields.Length || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != type.Fields.Length)
                        throw new InvalidFixtureException("meta-test-dto-shape");
                    return process.CreateRecord(typeId, type.Fields.Select(f => Decode(process, f.TypeId, value.GetProperty(f.Name), depth + 1)).ToArray());
                case MetaTypeKind.Array or MetaTypeKind.List:
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256) throw new InvalidFixtureException("meta-test-array-limit");
                    var items = value.EnumerateArray().Select(v => Decode(process, type.Args[0], v, depth + 1)).ToArray();
                    return type.Kind == MetaTypeKind.Array ? process.CreateArray(typeId, items) : process.CreateList(typeId, items);
                case MetaTypeKind.Opaque:
                    if (!MetaApiCatalog.Default.Types.Any(t => t.Name == type.Name && t.Kind == MetaTypeKind.Opaque)) throw new InvalidFixtureException("meta-test-opaque-type");
                    var name = value.GetString() ?? throw new InvalidFixtureException("meta-test-opaque-value");
                    if (name.Length > 96) throw new InvalidFixtureException("meta-test-opaque-limit");
                    if (!_opaque.TryGetValue((typeId, name), out var token))
                    {
                        if (_opaque.Count >= 128) throw new InvalidFixtureException("meta-test-opaque-limit");
                        token = (ulong) _opaque.Count + 1; _opaque[(typeId, name)] = token; _opaqueNames[token] = name;
                    }
                    return process.CreateOpaque(typeId, token);
                case MetaTypeKind.Option or MetaTypeKind.Result:
                    var tagName = value.GetProperty("tag").GetString(); var tag = type.Variants.SingleOrDefault(v => v.Name == tagName) ?? throw new InvalidFixtureException("meta-test-variant-tag");
                    var expectedFields = tag.PayloadTypeId == null ? 1 : 2;
                    if (value.EnumerateObject().Count() != expectedFields || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != expectedFields)
                        throw new InvalidFixtureException("meta-test-variant-shape");
                    return process.CreateVariant(typeId, tag.Tag, tag.PayloadTypeId is { } payload ? Decode(process, payload, value.GetProperty("value"), depth + 1) : null);
                default: throw new InvalidFixtureException("meta-test-forbidden-value");
            }
        }
    }
    private sealed class InvalidFixtureException(string code) : Exception(code);
}
