// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Decals;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Aggregates scheduler-driven external blood loss into bounded cleanable decals. This partial remains event-driven:
/// there is no world scan or per-frame puddle update, and moving patients naturally leave one staged mark per tile.
/// </summary>
public sealed partial class NCImpactBloodSystem
{
    [Dependency] private IGameTiming _timing = default!;

    private void InitializeBloodPools()
    {
        SubscribeLocalEvent<NCMedicalBodyComponent, NCBleedingChangedEvent>(OnExternalBloodLost);
    }

    private void OnExternalBloodLost(
        Entity<NCMedicalBodyComponent> body,
        ref NCBleedingChangedEvent args)
    {
        if (!float.IsFinite(args.ExternalBloodLost) || args.ExternalBloodLost <= 0f ||
            !TryComp<NCWoundContainerComponent>(body, out var wounds) ||
            !_prototypes.TryIndex(wounds.Profile, out NCWoundSystemProfilePrototype? woundSystem) ||
            !_prototypes.TryIndex(woundSystem.ImpactBloodProfile, out NCImpactBloodProfilePrototype? profile) ||
            profile.PoolStages.Count == 0 || profile.MaximumPoolTilesPerGrid <= 0)
        {
            return;
        }

        DepositBloodPool(body, args.ExternalBloodLost, profile);
    }

    private void DepositBloodPool(
        EntityUid body,
        float amount,
        NCImpactBloodProfilePrototype profile)
    {
        var bodyCoordinates = Transform(body).Coordinates;
        var mapCoordinates = _transform.ToMapCoordinates(bodyCoordinates);
        if (mapCoordinates.MapId == MapId.Nullspace ||
            _transform.GetGrid(bodyCoordinates) is not { Valid: true } grid ||
            !HasComp<DecalGridComponent>(grid))
        {
            return;
        }

        var local = _transform.ToCoordinates((grid, Transform(grid)), mapCoordinates).Position;
        var tile = new Vector2i((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));
        var pools = EnsureComp<NCBloodPoolGridComponent>(grid);

        if (!pools.Tiles.TryGetValue(tile, out var record))
        {
            EvictOldestPoolIfRequired(grid, pools, profile.MaximumPoolTilesPerGrid);
            record = new NCBloodPoolTileRecord
            {
                Position = ClampInsideTile(local, tile, profile.PoolTileMargin),
                Rotation = ResolvePoolRotation(tile),
                ModifiedAt = _timing.CurTime,
            };
            pools.Tiles[tile] = record;
        }
        else if (record.Stage >= 0 && !PoolDecalExists(grid, record))
        {
            // A mop removed the cleanable decal. The next real blood pulse starts a fresh visual pool rather than
            // resurrecting the previously cleaned volume at a higher stage.
            record.Volume = 0f;
            record.Stage = -1;
            record.DecalId = 0;
            record.Position = ClampInsideTile(local, tile, profile.PoolTileMargin);
            record.Rotation = ResolvePoolRotation(tile);
        }

        var maximum = float.IsFinite(profile.MaximumPoolVolumePerTile)
            ? MathF.Max(0f, profile.MaximumPoolVolumePerTile)
            : 0f;
        if (maximum <= 0f)
            return;

        record.Volume = Math.Clamp(record.Volume + amount, 0f, maximum);
        record.ModifiedAt = _timing.CurTime;
        var stage = ResolvePoolStage(record.Volume, profile.PoolStages);
        if (stage < 0 || stage == record.Stage && PoolDecalExists(grid, record))
            return;

        if (record.Stage >= 0)
            _decals.RemoveDecal(grid, record.DecalId);

        var definition = profile.PoolStages[stage];
        if (!_decals.TryAddDecal(
                definition.Decal.Id,
                new EntityCoordinates(grid, record.Position),
                out var decalId,
                profile.DecalColor,
                record.Rotation,
                zIndex: 4,
                cleanable: true))
        {
            record.Stage = -1;
            record.DecalId = 0;
            return;
        }

        record.Stage = stage;
        record.DecalId = decalId;
    }

    private void EvictOldestPoolIfRequired(
        EntityUid grid,
        NCBloodPoolGridComponent pools,
        int maximumTiles)
    {
        if (pools.Tiles.Count < maximumTiles)
            return;

        KeyValuePair<Vector2i, NCBloodPoolTileRecord>? oldest = null;
        foreach (var pair in pools.Tiles)
        {
            if (oldest == null || pair.Value.ModifiedAt < oldest.Value.Value.ModifiedAt)
                oldest = pair;
        }
        if (oldest == null)
            return;

        var record = oldest.Value.Value;
        if (record.Stage >= 0)
            _decals.RemoveDecal(grid, record.DecalId);
        pools.Tiles.Remove(oldest.Value.Key);
    }

    private bool PoolDecalExists(EntityUid grid, NCBloodPoolTileRecord record)
    {
        // DecalGridComponent deliberately hides its internal index. Query only the local chunk through DecalSystem's
        // public API so a mopped stain can be detected without walking every decal on the grid.
        foreach (var (index, _) in _decals.GetDecalsInRange(grid, record.Position, 1f))
        {
            if (index == record.DecalId)
                return true;
        }

        return false;
    }

    private static int ResolvePoolStage(float volume, IReadOnlyList<NCBloodPoolStage> stages)
    {
        var selected = -1;
        var selectedThreshold = float.NegativeInfinity;
        for (var index = 0; index < stages.Count; index++)
        {
            var threshold = stages[index].MinimumVolume;
            if (!float.IsFinite(threshold) || threshold < 0f || volume + 0.001f < threshold ||
                threshold <= selectedThreshold || string.IsNullOrEmpty(stages[index].Decal.Id))
            {
                continue;
            }

            selected = index;
            selectedThreshold = threshold;
        }
        return selected;
    }

    private static Vector2 ClampInsideTile(Vector2 position, Vector2i tile, float configuredMargin)
    {
        var margin = float.IsFinite(configuredMargin)
            ? Math.Clamp(configuredMargin, 0f, 0.49f)
            : 0.16f;
        return new Vector2(
            Math.Clamp(position.X, tile.X + margin, tile.X + 1f - margin),
            Math.Clamp(position.Y, tile.Y + margin, tile.Y + 1f - margin));
    }

    private static Angle ResolvePoolRotation(Vector2i tile)
    {
        // Stable integer mixing avoids an RNG dependency while keeping neighboring stains from repeating visibly.
        var hash = unchecked((uint) (tile.X * 73856093) ^ (uint) (tile.Y * 19349663));
        return new Angle((hash & 0xFFFFu) / 65535f * MathF.Tau);
    }
}
