// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.ActionResolution;

/// <summary>
/// Stable correlation ID for one server-authoritative action resolution.
/// The same ID follows every projectile, impact, wound and audit entry produced by that action.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCActionResolutionId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCActionResolutionId New()
    {
        return new NCActionResolutionId(Guid.NewGuid());
    }

    /// <summary>
    /// Derives a stable pseudo-random stream seed without depending on process-randomized hash codes.
    /// The resulting seed is snapshotted into physical action contexts for replayable resolution.
    /// </summary>
    public ulong DeriveSeed(uint stream = 0)
    {
        Span<byte> bytes = stackalloc byte[16];
        Value.TryWriteBytes(bytes);

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        hash ^= stream;
        hash *= prime;
        return hash;
    }

    /// <summary>
    /// Derives a stable persistence-safe record identifier for one result inside this action.
    /// This is identity generation, not a gameplay roll; equal action inputs reproduce the same identifier.
    /// </summary>
    public Guid DeriveGuid(uint primaryStream = 0, uint secondaryStream = 0)
    {
        Span<byte> input = stackalloc byte[24];
        Value.TryWriteBytes(input[..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(input[16..20], primaryStream);
        BinaryPrimitives.WriteUInt32LittleEndian(input[20..24], secondaryStream);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
