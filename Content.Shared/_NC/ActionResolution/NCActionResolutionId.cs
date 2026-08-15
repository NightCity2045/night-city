// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

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
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in Value.ToByteArray())
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
        // Content.Shared runs inside the Robust sandbox and must not depend on cryptographic APIs.
        // Two independently salted FNV streams provide stable 128-bit correlation identity; this is not
        // used for security, secrecy or gameplay randomness.
        // Keep this implementation on ordinary arrays and scalar values: Content.Shared is also passed
        // through ILVerify, which cannot verify every stackalloc/Span construction emitted by the compiler.
        var source = Value.ToByteArray();
        var low = DeriveFnv(source, primaryStream, secondaryStream, 14695981039346656037UL);
        var high = DeriveFnv(source, primaryStream, secondaryStream, 7809847782465536322UL);

        return new Guid(
            unchecked((int) low),
            unchecked((short) (low >> 32)),
            unchecked((short) (low >> 48)),
            (byte) high,
            (byte) (high >> 8),
            (byte) (high >> 16),
            (byte) (high >> 24),
            (byte) (high >> 32),
            (byte) (high >> 40),
            (byte) (high >> 48),
            (byte) (high >> 56));
    }

    private static ulong DeriveFnv(byte[] bytes, uint primaryStream, uint secondaryStream, ulong offset)
    {
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        for (var shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte) (primaryStream >> shift);
            hash *= prime;
        }

        for (var shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte) (secondaryStream >> shift);
            hash *= prime;
        }

        return hash;
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}
