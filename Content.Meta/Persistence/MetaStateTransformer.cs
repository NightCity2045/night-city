// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Hosting;

namespace Content.Meta.Persistence;

public enum MetaStateTransformKind : byte { Rename, Delete, SetDefault }
public sealed record MetaStateTransformStep(MetaStateTransformKind Kind, string Path, string Target = "", string Text = "");
public sealed record MetaStateTransformFile(string Path, byte[] Bytes, long Revision);

/// <summary>A bounded, declarative trusted migration. It cannot call guest code, host APIs or perform I/O.</summary>
public static class MetaStateTransformer
{
    public const int MaximumFiles = 1024;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumSteps = 128;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool TryTransform(IReadOnlyList<MetaStateTransformFile> source,
        IReadOnlyList<MetaStateTransformStep> steps, int capacity, out MetaStateTransformFile[] transformed)
    {
        transformed = [];
        if (source.Count > MaximumFiles || steps.Count > MaximumSteps || capacity < 0) return false;
        var files = new Dictionary<string, MetaStateTransformFile>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var file in source)
        {
            if (!MetaPaths.IsValid(file.Path) || file.Bytes.Length > 32768 || file.Revision < 1 ||
                !files.TryAdd(file.Path, file) || (bytes += file.Bytes.Length) > MaximumBytes) return false;
        }
        foreach (var step in steps)
        {
            if (!MetaPaths.IsValid(step.Path)) return false;
            switch (step.Kind)
            {
                case MetaStateTransformKind.Rename:
                    if (!MetaPaths.IsValid(step.Target) || step.Target == step.Path || step.Text.Length != 0) return false;
                    if (!files.TryGetValue(step.Path, out var previous)) break;
                    // An existing destination is data, not permission to overwrite it silently.
                    if (files.ContainsKey(step.Target) || previous.Revision == long.MaxValue) return false;
                    files.Remove(step.Path);
                    files.Add(step.Target, previous with { Path = step.Target, Revision = previous.Revision + 1 });
                    break;
                case MetaStateTransformKind.Delete:
                    if (step.Target.Length != 0 || step.Text.Length != 0) return false;
                    if (files.Remove(step.Path, out var deleted)) bytes -= deleted.Bytes.Length;
                    break;
                case MetaStateTransformKind.SetDefault:
                    if (step.Target.Length != 0 || step.Text.Length > 32768) return false;
                    byte[] value;
                    try { value = Utf8.GetBytes(step.Text); }
                    catch (EncoderFallbackException) { return false; }
                    if (value.Length > 32768) return false;
                    if (files.ContainsKey(step.Path)) break;
                    if (files.Count == MaximumFiles || (bytes += value.Length) > MaximumBytes) return false;
                    files.Add(step.Path, new(step.Path, value, 1));
                    break;
                default: return false;
            }
        }
        if (bytes > capacity) return false;
        transformed = files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return true;
    }
}
