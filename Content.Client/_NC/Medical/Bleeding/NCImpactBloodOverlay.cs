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
/// Draws short-lived pixel-sized droplets without spawning one entity per particle.
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
            for (var index = 0; index < burst.DropletCount; index++)
            {
                var direction = NCImpactBloodRules.SampleDirection(
                    burst.Direction,
                    burst.ConeDegrees,
                    burst.Seed,
                    (uint) index);
                var distanceSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x100u);
                var sizeSample = NCImpactBloodRules.Sample01(burst.Seed, (uint) index + 0x200u);
                var distance = burst.Distance * burst.DistanceMultiplier *
                               (0.35f + distanceSample * 0.65f) * travelProgress;
                var position = burst.Origin + direction * distance;
                var size = burst.DropletSize * (0.7f + sizeSample * 0.6f) * (1f - progress * 0.25f);
                var color = burst.Color.WithAlpha(burst.Color.A * alpha);
                handle.DrawRect(Box2.FromDimensions(position - new Vector2(size * 0.5f), new Vector2(size)), color);
            }
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
    float DistanceMultiplier,
    TimeSpan Duration,
    float Distance,
    float ConeDegrees,
    float DropletSize,
    Color Color,
    int MaximumActiveBursts,
    TimeSpan StartedAt);
