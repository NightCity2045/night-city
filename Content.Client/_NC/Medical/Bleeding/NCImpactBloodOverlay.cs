// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Bleeding;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Client._NC.Medical.Bleeding;

/// <summary>
/// Draws a bounded combination of irregular splatter sprites, stretched directional streaks and a few loose
/// droplets without spawning one entity per particle.
/// </summary>
public sealed class NCImpactBloodOverlay : Overlay
{
    private readonly IGameTiming _timing;
    private readonly List<NCImpactBloodBurst> _bursts = new();

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

    public NCImpactBloodOverlay(IGameTiming timing)
    {
        _timing = timing;
    }

    public void AddBurst(NCImpactBloodBurst burst)
    {
        PruneExpired();
        while (_bursts.Count >= burst.MaximumActiveBursts)
            _bursts.RemoveAt(0);
        _bursts.Add(burst);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        PruneExpired();
        var handle = args.WorldHandle;
        var now = _timing.CurTime;
        foreach (var burst in _bursts)
        {
            if (burst.MapId != args.MapId)
                continue;

            var elapsed = (float) (now - burst.StartedAt).TotalSeconds;
            var duration = (float) burst.Duration.TotalSeconds;
            if (duration <= 0f)
                continue;

            var progress = Math.Clamp(elapsed / duration, 0f, 1f);
            var travelProgress = 1f - (1f - progress) * (1f - progress);
            var alpha = Math.Clamp(1f - progress, 0f, 1f);
            DrawSplats(handle, burst, progress);
            DrawStreaks(handle, burst, progress);
            DrawDroplets(handle, burst, travelProgress, alpha, progress);
        }
    }

    private static void DrawSplats(
        DrawingHandleWorld handle,
        in NCImpactBloodBurst burst,
        float progress)
    {
        if (burst.SplatCount == 0 || burst.SplatTextures.Length == 0)
            return;

        // A few irregular masks replace the old circular cloud of independent micro-particles.
        var reveal = Math.Clamp(progress / 0.12f, 0f, 1f);
        var fade = 1f - Math.Clamp((progress - 0.28f) / 0.72f, 0f, 1f);
        for (var index = 0; index < burst.SplatCount; index++)
        {
            var direction = NCImpactBloodRules.SamplePatternDirection(
                burst.Direction,
                burst.PatternKind,
                burst.DirectionOffsetDegrees,
                burst.ConeDegrees,
                burst.Seed,
                (uint) index + 0x500u);
            var distanceSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x600u);
            var sizeSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x700u);
            var rotationSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x780u);
            var textureIndex = NCImpactBloodRules.PickIndex(
                burst.SplatTextures.Length,
                burst.Seed,
                (uint) index + 0x790u);
            var distance = burst.SplatRadius * (0.2f + distanceSample * 0.8f) * reveal;
            var position = burst.Origin + direction * distance;
            var size = burst.SplatSize * (0.72f + sizeSample * 0.42f) * (0.78f + reveal * 0.22f);
            var bounds = Box2.CenteredAround(position, new Vector2(size));
            var rotated = new Box2Rotated(bounds, new Angle(rotationSample * MathF.Tau), position);
            var color = burst.Color.WithAlpha(burst.Color.A * fade);
            handle.DrawTextureRect(burst.SplatTextures[textureIndex], rotated, color);
        }
    }

    private static void DrawStreaks(
        DrawingHandleWorld handle,
        in NCImpactBloodBurst burst,
        float progress)
    {
        if (burst.StreakCount == 0 || burst.StreakTextures.Length == 0)
            return;

        var headProgress = 1f - (1f - Math.Clamp(progress / 0.6f, 0f, 1f)) *
            (1f - Math.Clamp(progress / 0.6f, 0f, 1f));
        var fade = 1f - Math.Clamp((progress - 0.35f) / 0.65f, 0f, 1f);
        for (var index = 0; index < burst.StreakCount; index++)
        {
            var direction = NCImpactBloodRules.SamplePatternDirection(
                burst.Direction,
                burst.PatternKind,
                burst.DirectionOffsetDegrees,
                burst.ConeDegrees,
                burst.Seed,
                (uint) index + 0x800u);
            var distanceSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x900u);
            var widthSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0xA00u);
            var textureIndex = NCImpactBloodRules.PickIndex(
                burst.StreakTextures.Length,
                burst.Seed,
                (uint) index + 0xA80u);
            var length = burst.Distance * burst.DistanceMultiplier * burst.PatternDistanceMultiplier *
                         burst.StreakLengthMultiplier * (0.48f + distanceSample * 0.52f) * headProgress;
            var width = burst.StreakWidth * (0.78f + widthSample * 0.44f);
            if (length <= 0f || width <= 0f)
                continue;

            var midpoint = burst.Origin + direction * length * 0.5f;
            var box = new Box2(-width * 0.5f, -length * 0.5f, width * 0.5f, length * 0.5f);
            var rotated = new Box2Rotated(box.Translated(midpoint), direction.ToWorldAngle(), midpoint);
            var color = burst.Color.WithAlpha(burst.Color.A * fade);
            handle.DrawTextureRect(burst.StreakTextures[textureIndex], rotated, color);
        }
    }

    private static void DrawDroplets(
        DrawingHandleWorld handle,
        in NCImpactBloodBurst burst,
        float travelProgress,
        float alpha,
        float progress)
    {
        for (var index = 0; index < burst.DropletCount; index++)
        {
            var direction = NCImpactBloodRules.SamplePatternDirection(
                burst.Direction,
                burst.PatternKind,
                burst.DirectionOffsetDegrees,
                burst.ConeDegrees,
                burst.Seed,
                (uint) index + 0xB00u);
            var distanceSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0xC00u);
            var sizeSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0xD00u);
            var distance = burst.Distance * burst.DistanceMultiplier * burst.PatternDistanceMultiplier *
                           (0.35f + distanceSample * 0.65f) * travelProgress;
            var position = burst.Origin + direction * distance;
            var size = burst.DropletSize * (0.7f + sizeSample * 0.6f) * (1f - progress * 0.25f);
            var color = burst.Color.WithAlpha(burst.Color.A * alpha);
            handle.DrawRect(Box2.FromDimensions(position - new Vector2(size * 0.5f), new Vector2(size)), color);
        }
    }

    private void PruneExpired()
    {
        var now = _timing.CurTime;
        for (var index = _bursts.Count - 1; index >= 0; index--)
        {
            if (now - _bursts[index].StartedAt >= _bursts[index].Duration)
                _bursts.RemoveAt(index);
        }
    }
}

public readonly record struct NCImpactBloodBurst(
    MapId MapId,
    Vector2 Origin,
    Vector2 Direction,
    ulong Seed,
    byte DropletCount,
    byte SplatCount,
    byte StreakCount,
    Texture[] SplatTextures,
    Texture[] StreakTextures,
    float DistanceMultiplier,
    TimeSpan Duration,
    float Distance,
    NCImpactBloodPatternKind PatternKind,
    float ConeDegrees,
    float DirectionOffsetDegrees,
    float PatternDistanceMultiplier,
    float SplatRadius,
    float SplatSize,
    float StreakLengthMultiplier,
    float StreakWidth,
    float DropletSize,
    Color Color,
    int MaximumActiveBursts,
    TimeSpan StartedAt);
