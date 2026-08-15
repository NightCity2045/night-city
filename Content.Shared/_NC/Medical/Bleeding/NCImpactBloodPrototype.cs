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

    /// <summary>
    /// Visual shape for each open wound mechanism. Counts remain severity-dependent while geometry is
    /// mechanism-dependent, allowing a cut, a projectile exit and a blast to read differently.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<NCWoundType, NCImpactBloodPattern> Patterns { get; private set; } = new();

    [DataField(required: true)]
    public Color DecalColor { get; private set; }

    /// <summary>
    /// Fresh airborne blood is brighter than the persistent floor stain so the short effect remains readable.
    /// </summary>
    [DataField(required: true)]
    public Color EffectColor { get; private set; }

    [DataField(required: true)]
    public TimeSpan EffectDuration { get; private set; }

    [DataField(required: true)]
    public float SprayDistance { get; private set; }

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
/// Data-only geometry for one impact-blood mechanism. The server uses it for cleanable decal placement and the
/// client copies the same values into a bounded transient burst.
/// </summary>
[DataDefinition]
public sealed partial class NCImpactBloodPattern
{
    [DataField(required: true)]
    public NCImpactBloodPatternKind Kind { get; private set; }

    /// <summary>
    /// Existing tintable decal sprites used for irregular impact marks. NC owns only the selection policy.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<DecalPrototype>> SplatDecals { get; private set; } = new();

    /// <summary>
    /// Existing tintable line decals stretched along the resolved impact direction.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<DecalPrototype>> StreakDecals { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, int> SplatsBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCWoundSeverity, int> StreaksBySeverity { get; private set; } = new();

    [DataField(required: true)]
    public float ConeDegrees { get; private set; }

    [DataField]
    public float DirectionOffsetDegrees { get; private set; }

    [DataField]
    public float DistanceMultiplier { get; private set; } = 1f;

    [DataField]
    public float SplatRadius { get; private set; } = 0.16f;

    [DataField]
    public float SplatSize { get; private set; } = 0.6f;

    [DataField]
    public float StreakLengthMultiplier { get; private set; } = 1f;

    [DataField]
    public float StreakWidth { get; private set; } = 0.22f;

    /// <summary>
    /// Fraction of persistent marks that use a directional streak instead of a compact splatter.
    /// </summary>
    [DataField]
    public float PersistentStreakFraction { get; private set; } = 0.5f;
}

public enum NCImpactBloodPatternKind : byte
{
    Directed,
    Arc,
    Radial,
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
        var halfCone = Math.Clamp(coneDegrees, 0f, 360f) * MathF.PI / 360f;
        var offset = (Sample01(seed, stream) * 2f - 1f) * halfCone;
        var sin = MathF.Sin(offset);
        var cos = MathF.Cos(offset);
        return new Vector2(
            direction.X * cos - direction.Y * sin,
            direction.X * sin + direction.Y * cos);
    }

    /// <summary>
    /// Resolves one deterministic ray for directed projectile spray, a cross-cut arc or a radial blast.
    /// </summary>
    public static Vector2 SamplePatternDirection(
        Vector2 direction,
        NCImpactBloodPatternKind kind,
        float directionOffsetDegrees,
        float coneDegrees,
        ulong seed,
        uint stream)
    {
        if (kind == NCImpactBloodPatternKind.Radial)
        {
            var angle = Sample01(seed, stream) * MathF.Tau;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        direction = direction.IsLengthZero() ? Vector2.UnitY : direction.Normalized();
        var offset = directionOffsetDegrees;
        if (kind == NCImpactBloodPatternKind.Arc)
            offset += 90f;

        var radians = offset * MathF.PI / 180f;
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        var rotated = new Vector2(
            direction.X * cos - direction.Y * sin,
            direction.X * sin + direction.Y * cos);
        return SampleDirection(rotated, coneDegrees, seed, stream);
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

    /// <summary>
    /// Selects a stable index without relying on collection ordering outside the explicitly ordered YAML list.
    /// </summary>
    public static int PickIndex(int count, ulong seed, uint stream)
    {
        if (count <= 1)
            return 0;

        return Math.Min(count - 1, (int) (Sample01(seed, stream) * count));
    }
}
