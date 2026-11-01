// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed record DiagnosticArtifact(string Id, string Path, string MediaType, long Bytes, string Sha256);

public sealed class ArtifactStore(WorkspacePaths paths)
{
    private const long MaximumStoreBytes = 512 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DiagnosticArtifact> WriteJsonAsync(string category, object value, CancellationToken cancellationToken) =>
        await WriteAsync(category, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticProtocol.JsonOptions), cancellationToken);

    public async Task<DiagnosticArtifact> WriteAsync(string category, string mediaType, byte[] bytes, CancellationToken cancellationToken)
    {
        if (!category.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_') || category.Length is < 1 or > 40)
            throw new DiagnosticException("invalid_arguments", "Invalid artifact category.");
        if (bytes.Length > 16 * 1024 * 1024)
            throw new DiagnosticException("artifact_too_large", "An artifact may not exceed 16 MiB.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.ArtifactRoot);
            var usedBytes = new DirectoryInfo(paths.ArtifactRoot).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Sum(x => x.Length);
            if (usedBytes + bytes.Length > MaximumStoreBytes)
                throw new DiagnosticException("artifact_quota", "Artifact quota reached. Remove old run artifacts explicitly before continuing.");
            var id = category + "-" + Guid.NewGuid().ToString("N");
            var extension = mediaType switch { "image/png" => ".png", "text/html" => ".html", _ => ".json" };
            var path = paths.Resolve(Path.Combine("artifacts", "ss14-mcp", id + extension));
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return new DiagnosticArtifact(id, path, mediaType, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public DiagnosticArtifact[] List()
    {
        if (!Directory.Exists(paths.ArtifactRoot))
            return [];
        return new DirectoryInfo(paths.ArtifactRoot).EnumerateFiles().OrderByDescending(x => x.LastWriteTimeUtc).Take(200)
            .Select(x => new DiagnosticArtifact(Path.GetFileNameWithoutExtension(x.Name), x.FullName,
                x.Extension switch { ".png" => "image/png", ".html" => "text/html", _ => "application/json" }, x.Length, ""))
            .ToArray();
    }

    public async Task<string> ReadJsonAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length is < 1 or > 100 || !id.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'))
            throw new DiagnosticException("invalid_arguments", "Invalid artifact identifier.");
        var path = paths.Resolve(Path.Combine("artifacts", "ss14-mcp", id + ".json"));
        if (!File.Exists(path))
            throw new DiagnosticException("artifact_not_found", "The JSON artifact does not exist.");
        await using var stream = File.OpenRead(path);
        return Encoding.UTF8.GetString(await DiagnosticWire.ReadBoundedAsync(stream, 16 * 1024 * 1024, cancellationToken));
    }

    public async Task<byte[]> ReadImageAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length is < 1 or > 100 || !id.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'))
            throw new DiagnosticException("invalid_arguments", "Invalid image artifact identifier.");
        var path = paths.Resolve(Path.Combine("artifacts", "ss14-mcp", id + ".png"));
        if (!File.Exists(path))
            throw new DiagnosticException("artifact_not_found", "The PNG artifact does not exist.");
        await using var stream = File.OpenRead(path);
        return await DiagnosticWire.ReadBoundedAsync(stream, DiagnosticProtocol.MaximumResponseBytes, cancellationToken);
    }
}
