// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NightCity.Diagnostics.Contracts;

public sealed record DiagnosticPipeRequest(string Token, DiagnosticRequest Request);

public static class DiagnosticWire
{
    public static bool Authenticate(string expected, string? actual)
    {
        if (expected.Length < 32 || actual == null || actual.Length > 256)
            return false;
        // Fixed-size hashes avoid leaking the token length through the comparison.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
    }

    public static async ValueTask WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticProtocol.JsonOptions);
        if (bytes.Length > maximumBytes)
            throw new DiagnosticException("response_too_large", "The message exceeds its configured byte limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > maximumBytes)
            throw new DiagnosticException("invalid_message_size", "Invalid framed message size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, DiagnosticProtocol.JsonOptions)
            ?? throw new DiagnosticException("invalid_message", "The peer sent an empty message.");
    }

    public static async ValueTask<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0)
                return buffer.ToArray();
            if (buffer.Length + count > maximumBytes)
                throw new DiagnosticException("message_too_large", "The message exceeds its configured byte limit.");
            buffer.Write(chunk, 0, count);
        }
    }
}
