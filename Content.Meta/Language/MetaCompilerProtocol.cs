// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Buffers.Binary;
using System.Text.Json;

namespace Content.Meta.Language;

/// <summary>One length-prefixed immutable request and one bounded response. No paths or credentials are transmitted.</summary>
public static class MetaCompilerProtocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 8 * 1024 * 1024;
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("META IPC frame exceeds limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellation); await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > maximumBytes) throw new InvalidDataException("Invalid META IPC frame length.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty META IPC object.");
    }
}

public sealed record MetaWorkerRequest(int ProtocolVersion, string CatalogHash, MetaCompilationRequest Compilation);
public sealed record MetaWorkerResponse(int ProtocolVersion, byte[]? Artifact, string? ArtifactHash, string CacheIdentity,
    MetaDiagnostic[] Diagnostics, MetaFunctionAnalysis[] Analysis);
