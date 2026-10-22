// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed class ResourceInspector(WorkspacePaths paths)
{
    public async Task<JsonObject> InspectAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var resource = arguments.RequiredString("path", 512).Replace('\\', '/').TrimStart('/');
        if (resource.StartsWith("Resources/", StringComparison.Ordinal))
            resource = resource[10..];
        var path = paths.Resolve("Resources/" + resource);
        var resourceRoot = paths.Resolve("Resources") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(resourceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new DiagnosticException("invalid_resource", "Resource inspection is limited to the Resources tree.");
        if (Directory.Exists(path))
        {
            if (!path.EndsWith(".rsi", StringComparison.OrdinalIgnoreCase))
                throw new DiagnosticException("unsupported_resource", "Directory inspection supports RSI packages only.");
            var metadata = paths.Resolve(Path.GetRelativePath(paths.Root, Path.Combine(path, "meta.json")));
            var bytes = await ReadFileAsync(metadata, 128 * 1024, cancellationToken);
            var states = Directory.EnumerateFiles(path, "*.png").Take(513).Select(Path.GetFileName).ToArray();
            return new JsonObject { ["kind"] = "rsi", ["path"] = path, ["metadata"] = JsonNode.Parse(bytes),
                ["images"] = System.Text.Json.JsonSerializer.SerializeToNode(states.Take(512)), ["truncated"] = states.Length > 512,
                ["validationPerformed"] = false };
        }
        if (!File.Exists(path))
            throw new DiagnosticException("resource_not_found", "The resource does not exist.");
        var info = new FileInfo(path);
        var result = new JsonObject { ["path"] = path, ["bytes"] = info.Length,
            ["modifiedAt"] = info.LastWriteTimeUtc.ToString("O"), ["validationPerformed"] = false };
        switch (info.Extension.ToLowerInvariant())
        {
            case ".json":
                result["data"] = JsonNode.Parse(await ReadFileAsync(path, 128 * 1024, cancellationToken));
                break;
            case ".yml":
            case ".yaml":
            case ".ftl":
            case ".txt":
                result["text"] = Encoding.UTF8.GetString(await ReadFileAsync(path, 128 * 1024, cancellationToken));
                break;
            case ".png":
                if (info.Length < 24)
                    throw new DiagnosticException("invalid_resource", "The PNG header is incomplete.");
                await using (var stream = File.OpenRead(path))
                {
                    var header = new byte[24];
                    await stream.ReadExactlyAsync(header, cancellationToken);
                    if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new DiagnosticException("invalid_resource", "The resource has an invalid PNG signature.");
                    result["width"] = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
                    result["height"] = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
                }
                break;
            default:
                result["coverage"] = "file-metadata-only";
                break;
        }
        return result;
    }

    private static async Task<byte[]> ReadFileAsync(string path, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new DiagnosticException("resource_not_found", "The resource metadata does not exist.");
        await using var stream = File.OpenRead(path);
        return await DiagnosticWire.ReadBoundedAsync(stream, limit, cancellationToken);
    }
}
