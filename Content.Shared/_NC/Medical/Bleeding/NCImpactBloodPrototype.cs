// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Decals;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Bleeding;

/// <summary>
/// Data-defined initial blood pulse and bounded visual policy for one accepted organic wound.
/// Ongoing loss remains owned by the ordinary wound bleeding sources.
/// </summary>
[Prototype("ncImpactBloodProfile")]
public sealed partial class NCImpactBloodProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public HashSet<NCWoundType> WoundTypes { get; private set; } = new();

    /// <summary>
    /// Immediate authoritative circulation loss in NC blood-volume units.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, float> BloodLossBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, int> DropletsBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, int> DecalsBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public ProtoId<DecalPrototype> Decal { get; private set; }

    [DataField(required: true)]
    public Color Color { get; private set; }

    [DataField(required: true)]
    public TimeSpan EffectDuration { get; private set; }

    [DataField(required: true)]
    public float SprayDistance { get; private set; }

    [DataField(required: true)]
    public float SprayConeDegrees { get; private set; }

    [DataField(required: true)]
    public float DropletSize { get; private set; }

    [DataField]
    public float EmbeddedDistanceMultiplier { get; private set; } = 0.65f;

    [DataField]
    public float ExitDistanceMultiplier { get; private set; } = 1.35f;

    [DataField]
    public int MaximumDecalsPerImpact { get; private set; } = 2;

    [DataField]
    public int MaximumNearbyDecals { get; private set; } = 6;

    [DataField]
    public float NearbyDecalRadius { get; private set; } = 0.9f;

    [DataField]
    public int MaximumClientBursts { get; private set; } = 64;
}

/// <summary>
/// Pure deterministic sampling shared by the server decal placement and the client transient effect.
/// </summary>
public static class NCImpactBloodRules
{
    public static float Sample01(ulong seed, uint stream)
    {
        var value = seed + 0x9E3779B97F4A7C15UL * (stream + 1UL);
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return (value >> 40) / 16777216f;
    }

    public static Vector2 SampleDirection(Vector2 direction, float coneDegrees, ulong seed, uint stream)
    {
        direction = direction.IsLengthZero() ? Vector2.UnitY : direction.Normalized();
        var halfCone = Math.Clamp(coneDegrees, 0f, 180f) * MathF.PI / 360f;
        var offset = (Sample01(seed, stream) * 2f - 1f) * halfCone;
        var sin = MathF.Sin(offset);
        var cos = MathF.Cos(offset);
        return new Vector2(
            direction.X * cos - direction.Y * sin,
            direction.X * sin + direction.Y * cos);
    }

    public static float ResolveDistanceMultiplier(
        NCImpactBloodProfilePrototype profile,
        NCTraumaProjectileDisposition disposition)
    {
        return disposition switch
        {
            NCTraumaProjectileDisposition.Embedded => profile.EmbeddedDistanceMultiplier,
            NCTraumaProjectileDisposition.Exit => profile.ExitDistanceMultiplier,
            _ => 1f,
        };
    }
}
