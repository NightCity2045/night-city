// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;

namespace Content.Meta.Persistence;

/// <summary>Blob roots in native durable metadata. Container/API hashes are not byte-store identities.</summary>
public static class MetaBlobReferences
{
    // Increment when adding a durable metadata root shape; maintenance re-scans legacy owners.
    public const int PolicyVersion = 2;
    public const int MaximumPayloadBytes = 2 * 1024 * 1024;
    public const int MaximumReferences = 32768;
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "meta.project", "meta.installation", "meta.installation-stage", "meta.volume", "meta.media",
        "meta.inherited-images", "meta.software.product", "meta.software.publication",
        "meta.software.publisher-image", "meta.software.license", "meta.software.copy-evidence",
        "meta.software.inheritance",
        "meta.development-contract", "meta.development.delivery", "meta.development.delivery-claim",
    };

    public static bool IsTracked(string aggregateType) => Types.Contains(aggregateType);

    public static string[] Extract(string aggregateType, string payload)
    {
        if (!IsTracked(aggregateType)) return [];
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaximumPayloadBytes || Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
            throw new InvalidDataException("META blob reference metadata exceeds its byte limit.");
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        var pending = new Stack<(JsonElement Value, bool SemanticHashes)>();
        pending.Push((document.RootElement, false));
        var references = new HashSet<string>(StringComparer.Ordinal);
        var nodes = 0;
        while (pending.TryPop(out var item))
        {
            if (++nodes > 262144) throw new InvalidDataException("META blob reference metadata exceeds its node limit.");
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in item.Value.EnumerateArray()) pending.Push((element, item.SemanticHashes));
                continue;
            }
            if (item.Value.ValueKind != JsonValueKind.Object) continue;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.Value.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new InvalidDataException("Duplicate META metadata field.");
                var root = property.Name is "Digest" or "BlobDigest" or "ReleaseDigest" or "PinnedArtifactDigest" ||
                           property.Name == "ArtifactDigest" && !item.SemanticHashes;
                if (root && property.Value.ValueKind != JsonValueKind.Null)
                {
                    if (property.Value.ValueKind != JsonValueKind.String || !IsDigest(property.Value.GetString()))
                        throw new InvalidDataException("Invalid META blob identity.");
                    references.Add(property.Value.GetString()!);
                    if (references.Count > MaximumReferences) throw new InvalidDataException("Too many META blob references.");
                }
                else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    pending.Push((property.Value, item.SemanticHashes || property.Name is "Dependencies" or "Exports"));
                }
            }
        }
        return references.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
