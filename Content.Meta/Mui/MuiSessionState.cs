// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Content.Meta.Mui;

public enum MuiUpdateResult : byte { Applied, Duplicate, Gap, Invalid, Closed }

/// <summary>A session-local projection. Invalid updates never partially mutate data or advance the revision.</summary>
public sealed class MuiSessionState : IDisposable
{
    private readonly IReadOnlyDictionary<string, MuiField> _schema;
    private JsonObject _state = new();
    public Guid SessionId { get; }
    public long Revision { get; private set; } = -1;
    public bool Closed { get; private set; }

    public MuiSessionState(Guid sessionId, IReadOnlyDictionary<string, MuiField> schema)
    {
        SessionId = sessionId;
        _schema = schema;
    }

    public MuiUpdateResult TryApplySnapshot(long revision, string json)
    {
        if (Closed)
            return MuiUpdateResult.Closed;
        if (revision < 0 || !MuiDataValidator.TryValidate(json, _schema, MuiLimits.InitialStateBytes, out _))
            return MuiUpdateResult.Invalid;
        if (revision < Revision)
            return MuiUpdateResult.Duplicate;
        var candidate = JsonNode.Parse(json)!.AsObject();
        if (revision == Revision)
            return JsonNode.DeepEquals(_state, candidate) ? MuiUpdateResult.Duplicate : MuiUpdateResult.Invalid;
        _state = candidate;
        Revision = revision;
        return MuiUpdateResult.Applied;
    }

    /// <summary>Patch JSON: {"set":{"field":value},"remove":["optionalField"]}. Nested object fields are atomic values.</summary>
    public MuiUpdateResult TryApplyPatch(long baseRevision, long revision, string json)
    {
        if (Closed)
            return MuiUpdateResult.Closed;
        if (revision <= Revision)
            return MuiUpdateResult.Duplicate;
        if (baseRevision != Revision || revision != baseRevision + 1)
            return MuiUpdateResult.Gap;
        if (Encoding.UTF8.GetByteCount(json) > MuiLimits.PatchBytes)
            return MuiUpdateResult.Invalid;
        try
        {
            using var patch = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MuiLimits.Depth });
            var root = patch.RootElement;
            var budget = MuiLimits.Nodes;
            if (root.ValueKind != JsonValueKind.Object || !MuiDataValidator.ValidateBudget(root, ref budget))
                return MuiUpdateResult.Invalid;
            var candidate = (JsonObject) _state.DeepClone();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in root.EnumerateObject())
            {
                if (operation.Name == "set" && operation.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in operation.Value.EnumerateObject())
                    {
                        if (!touched.Add(field.Name) || !_schema.TryGetValue(field.Name, out var schema) || !MuiDataValidator.Matches(field.Value, schema))
                            return MuiUpdateResult.Invalid;
                        candidate[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                    }
                }
                else if (operation.Name == "remove" && operation.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in operation.Value.EnumerateArray())
                    {
                        if (field.ValueKind != JsonValueKind.String || !touched.Add(field.GetString()!) ||
                            !_schema.TryGetValue(field.GetString()!, out var schema) || schema.Required)
                            return MuiUpdateResult.Invalid;
                        candidate.Remove(field.GetString()!);
                    }
                }
                else
                    return MuiUpdateResult.Invalid;
            }
            if (!MuiDataValidator.TryValidate(candidate.ToJsonString(), _schema, MuiLimits.InitialStateBytes, out _))
                return MuiUpdateResult.Invalid;
            _state = candidate;
            Revision = revision;
            return MuiUpdateResult.Applied;
        }
        catch (JsonException) { return MuiUpdateResult.Invalid; }
        catch (ArgumentException exception) when (exception.InnerException is EncoderFallbackException) { return MuiUpdateResult.Invalid; }
        catch (InvalidOperationException) { return MuiUpdateResult.Invalid; }
    }

    public JsonNode? Read(string path)
    {
        if (Closed || !MuiBinding.IsPath(path))
            return null;
        // An explicitly declared dotted projection key takes precedence over nested traversal.
        if (_state.TryGetPropertyValue(path, out var exact))
            return exact?.DeepClone();
        JsonNode? value = _state;
        foreach (var segment in path.Split('.'))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value))
                return null;
        }
        return value?.DeepClone();
    }

    public void Dispose()
    {
        _state.Clear();
        Closed = true;
    }
}
