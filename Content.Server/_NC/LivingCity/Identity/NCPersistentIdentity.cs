// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Produces stable command and correlation identities so retried journal writes stay idempotent.
/// </summary>
public static class NCPersistentIdentity
{
    private static readonly Guid NamespaceId = new("34ef622e-f578-50c5-a83a-e3cfe67eed86");

    public static Guid Command(string domain, string operation, string stableKey) =>
        CreateGuid($"command/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    public static Guid Correlation(string domain, string operation, string stableKey) =>
        CreateGuid($"correlation/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    private static Guid CreateGuid(string value)
    {
        Span<byte> scope = stackalloc byte[16];
        NamespaceId.TryWriteBytes(scope);
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[scope.Length + text.Length];
        scope.CopyTo(input);
        text.CopyTo(input.AsSpan(scope.Length));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // Stable UUIDv5-shaped value; SHA-256 is used because the identity is local and not an RFC UUID namespace.
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }

}
