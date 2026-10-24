// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.ContentPack;

namespace Content.Server._NC.Meta.Projects;

public sealed record NCMetaBuiltinProject(string Id, string Name, string NameRu, IReadOnlyDictionary<string, string> Files);

/// <summary>Only the shipped resource catalog supplies template paths. A UI request supplies an ID, never a path.</summary>
public sealed partial class NCMetaBuiltinCatalogSystem : EntitySystem
{
    [Dependency] private IResourceManager _resources = default!;
    private readonly Dictionary<string, NCMetaBuiltinProject> _projects = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public override void Initialize()
    {
        base.Initialize();
        try
        {
            using var catalog = JsonDocument.Parse(ReadResource("Meta/Projects/catalog.json", 16384), new JsonDocumentOptions { MaxDepth = 4 });
            if (catalog.RootElement.GetProperty("version").GetString() != "1.0") throw new JsonException();
            var entries = catalog.RootElement.GetProperty("projects").EnumerateArray().ToArray();
            if (entries.Length > 64) throw new JsonException();
            foreach (var entry in entries)
            {
                var id = entry.GetProperty("id").GetString() ?? "";
                var manifest = entry.GetProperty("manifest").GetString() ?? "";
                if (id.Length is < 1 or > 64 || !manifest.StartsWith("Resources/Meta/Projects/", StringComparison.Ordinal) || !MetaPaths.IsValid(manifest))
                    throw new JsonException();
                var folder = "Meta/Projects/" + id + "/";
                if (manifest != "Resources/" + folder + "manifest.json") throw new JsonException();
                var manifestText = ReadResource(folder + "manifest.json", 65536);
                using var document = JsonDocument.Parse(manifestText, new JsonDocumentOptions { MaxDepth = 12 });
                var root = document.RootElement;
                if (root.GetProperty("version").GetString() != "1.0" || root.GetProperty("id").GetString() != id) throw new JsonException();
                var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["manifest.json"] = manifestText };
                foreach (var file in root.GetProperty("files").EnumerateArray())
                {
                    var path = file.GetProperty("path").GetString() ?? "";
                    var source = file.GetProperty("sourcePath").GetString() ?? "";
                    var hash = file.GetProperty("sha256").GetString() ?? "";
                    if (!MetaPaths.IsValid(path) || !MetaPaths.IsValid(source) || !source.StartsWith("Resources/Meta/", StringComparison.Ordinal) || files.Count >= 62)
                        throw new JsonException();
                    var text = new SourceText(path, ReadResource(source[10..], 256 * 1024));
                    if (text.Revision != hash || !files.TryAdd(path, GameTemplateSource(path, text.Text))) throw new JsonException();
                }
                var gameManifest = JsonNode.Parse(manifestText) ?? throw new JsonException();
                foreach (var file in gameManifest["files"]!.AsArray())
                {
                    var path = file!["path"]!.GetValue<string>();
                    file["sha256"] = new SourceText(path, files[path]).Revision;
                }
                files["manifest.json"] = gameManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                var fixture = root.GetProperty("testFixture").GetString() ?? "";
                if (!MetaPaths.IsValid(fixture) || !files.TryAdd(fixture, ReadResource(folder + fixture, 65536))) throw new JsonException();
                if (files.Values.Sum(value => (long) Utf8.GetByteCount(value)) > 1024 * 1024) throw new JsonException();
                _projects.Add(id, new(id, root.GetProperty("name").GetString() ?? id, root.GetProperty("nameRu").GetString() ?? id,
                    new ReadOnlyDictionary<string, string>(files)));
            }
        }
        catch (Exception exception)
        {
            _projects.Clear();
            Log.Error($"META shipped project catalog failed integrity validation: {exception}");
        }
    }
    public NCMetaTemplateSummary[] GetSummaries() => _projects.Values.OrderBy(value => value.Id, StringComparer.Ordinal)
        .Select(value => new NCMetaTemplateSummary(value.Id, value.Name, value.NameRu)).ToArray();
    public bool TryGet(string id, out NCMetaBuiltinProject project) => _projects.TryGetValue(id, out project!);
    private static string GameTemplateSource(string path, string source)
    {
        if (!path.EndsWith(".meta", StringComparison.Ordinal) && !path.EndsWith(".mui", StringComparison.Ordinal)) return source;
        var lineEnd = source.IndexOf('\n');
        var firstLine = lineEnd < 0 ? source : source[..lineEnd].TrimEnd('\r');
        if (!firstLine.StartsWith("// SPDX-License-Identifier:", StringComparison.Ordinal) &&
            !firstLine.StartsWith("<!-- SPDX-License-Identifier:", StringComparison.Ordinal)) return source;
        var content = lineEnd < 0 ? "" : source[(lineEnd + 1)..];
        return content.StartsWith('\n') ? content[1..] : content;
    }
    private string ReadResource(string path, int maximumBytes)
    {
        using var stream = _resources.ContentFileRead("/" + path);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int) bytes.Length))) > 0)
        {
            bytes.Write(buffer, 0, count);
            if (bytes.Length > maximumBytes) throw new InvalidDataException("META resource byte limit.");
        }
        return Utf8.GetString(bytes.ToArray());
    }
}
