// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared.Decals;
using Content.Shared.Doors.Components;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Handles persistent impact marks attached to structures. The index contains only actually stained entities and
/// is bounded by the active profile; physics queries run once per accepted impact, never from Update.
/// </summary>
public sealed partial class NCImpactBloodSystem
{
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly ProtoId<TagPrototype> WindowTag = "Window";
    private const float MinimumSurfaceStainSize = 0.04f;
    private const float MaximumSurfaceStainSize = 0.9f;
    private const float SurfaceHalfExtent = 0.48f;

    [Dependency] private SharedPhysicsSystem _surfacePhysics = default!;
    [Dependency] private TagSystem _surfaceTags = default!;

    private readonly LinkedList<EntityUid> _surfaceOrder = [];
    private readonly Dictionary<EntityUid, LinkedListNode<EntityUid>> _surfaceNodes = [];

    private void InitializeSurfaceStains()
    {
        SubscribeLocalEvent<NCBloodStainSurfaceComponent, ComponentStartup>(OnSurfaceStartup);
        SubscribeLocalEvent<NCBloodStainSurfaceComponent, ComponentShutdown>(OnSurfaceShutdown);
    }

    private void OnSurfaceStartup(Entity<NCBloodStainSurfaceComponent> entity, ref ComponentStartup args)
    {
        if (entity.Comp.Stains.Count > 0)
            TouchSurface(entity.Owner);
    }

    private void OnSurfaceShutdown(Entity<NCBloodStainSurfaceComponent> entity, ref ComponentShutdown args)
    {
        UntrackSurface(entity.Owner);
    }

    /// <summary>
    /// Allows the ordinary forensics cleaner to recognize a runtime-stained structure without adding a permanent
    /// forensic component to every wall prototype.
    /// </summary>
    public bool HasSurfaceStains(EntityUid uid)
    {
        return TryComp<NCBloodStainSurfaceComponent>(uid, out var surface) && surface.Stains.Count > 0;
    }

    /// <summary>
    /// Clears all attached marks after the vanilla forensic cleaning DoAfter succeeds.
    /// </summary>
    public bool TryCleanSurface(EntityUid uid)
    {
        if (!TryComp<NCBloodStainSurfaceComponent>(uid, out var surface) || surface.Stains.Count == 0)
            return false;

        surface.Stains.Clear();
        Dirty(uid, surface);
        UntrackSurface(uid);
        RemCompDeferred<NCBloodStainSurfaceComponent>(uid);
        return true;
    }

    /// <summary>
    /// Projects one selected impact mark onto the first closed wall, window or door before its floor endpoint.
    /// Returns false when the ray remains unobstructed so the caller can create an ordinary floor decal instead.
    /// </summary>
    private bool TryDepositSurfaceStain(
        MapCoordinates origin,
        Vector2 direction,
        float distance,
        ProtoId<DecalPrototype> decal,
        Angle worldAngle,
        Vector2 requestedSize,
        NCImpactBloodProfilePrototype profile)
    {
        if (origin.MapId == MapId.Nullspace ||
            !IsFinite(direction) ||
            direction.IsLengthZero() ||
            !float.IsFinite(distance) ||
            distance <= 0f ||
            !IsFinite(requestedSize) ||
            profile.MaximumSurfaceStainsPerEntity <= 0 ||
            profile.MaximumActiveSurfaceEntities <= 0 ||
            !float.IsFinite(profile.SurfaceStainScale) ||
            profile.SurfaceStainScale <= 0f)
        {
            return false;
        }

        var ray = new CollisionRay(
            origin.Position,
            direction.Normalized(),
            (int) CollisionGroup.BulletImpassable);
        RayCastResults? hit = null;
        foreach (var result in _surfacePhysics.IntersectRayWithPredicate(
                     origin.MapId,
                     ray,
                     maxLength: distance,
                     predicate: entity => !IsStainableSurface(entity),
                     returnOnFirstHit: true))
        {
            hit = result;
            break;
        }

        if (hit is not { } surfaceHit || Deleted(surfaceHit.HitEntity))
            return false;

        var surfaceUid = surfaceHit.HitEntity;
        var surfaceTransform = Transform(surfaceUid);
        var hadComponent = HasComp<NCBloodStainSurfaceComponent>(surfaceUid);
        if (!hadComponent)
            EvictSurfacesFor(profile.MaximumActiveSurfaceEntities);

        var surface = EnsureComp<NCBloodStainSurfaceComponent>(surfaceUid);
        var localCoordinates = _transform.ToCoordinates(
            (surfaceUid, surfaceTransform),
            new MapCoordinates(surfaceHit.HitPos, origin.MapId));
        var surfaceRotation = _transform.GetWorldRotation(surfaceTransform);
        var size = new Vector2(
            Math.Clamp(requestedSize.X, MinimumSurfaceStainSize, MaximumSurfaceStainSize),
            Math.Clamp(requestedSize.Y, MinimumSurfaceStainSize, MaximumSurfaceStainSize));
        var localPosition = new Vector2(
            Math.Clamp(
                localCoordinates.Position.X,
                -SurfaceHalfExtent + size.X * 0.5f,
                SurfaceHalfExtent - size.X * 0.5f),
            Math.Clamp(
                localCoordinates.Position.Y,
                -SurfaceHalfExtent + size.Y * 0.5f,
                SurfaceHalfExtent - size.Y * 0.5f));
        var localRotation = (float) (worldAngle - surfaceRotation).Reduced().Theta;

        while (surface.Stains.Count >= profile.MaximumSurfaceStainsPerEntity)
            surface.Stains.RemoveAt(0);

        surface.Stains.Add(new NCBloodSurfaceStain(
            decal,
            localPosition,
            localRotation,
            size,
            profile.DecalColor));
        Dirty(surfaceUid, surface);
        TouchSurface(surfaceUid);
        return true;
    }

    private bool IsStainableSurface(EntityUid uid)
    {
        return !Deleted(uid) &&
               (_surfaceTags.HasTag(uid, WallTag) ||
                _surfaceTags.HasTag(uid, WindowTag) ||
                HasComp<DoorComponent>(uid));
    }

    private void EvictSurfacesFor(int maximumActive)
    {
        while (_surfaceNodes.Count >= maximumActive && _surfaceOrder.First is { } first)
        {
            var uid = first.Value;
            UntrackSurface(uid);
            RemComp<NCBloodStainSurfaceComponent>(uid);
        }
    }

    private void TouchSurface(EntityUid uid)
    {
        if (_surfaceNodes.Remove(uid, out var existing))
            _surfaceOrder.Remove(existing);

        var node = _surfaceOrder.AddLast(uid);
        _surfaceNodes.Add(uid, node);
    }

    private void UntrackSurface(EntityUid uid)
    {
        if (!_surfaceNodes.Remove(uid, out var node))
            return;

        _surfaceOrder.Remove(node);
    }
}
