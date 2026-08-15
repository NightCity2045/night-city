// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Bleeding;
using Content.Shared.Decals;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Bleeding;

/// <summary>
/// Maintains an event-driven index of stained structures and a decal-texture cache. The overlay never scans all
/// walls or doors in the world.
/// </summary>
public sealed partial class NCBloodStainSurfaceClientSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SpriteSystem _sprites = default!;

    private readonly HashSet<EntityUid> _stainedEntities = [];
    private readonly Dictionary<ProtoId<DecalPrototype>, Texture> _textures = [];
    private NCBloodStainSurfaceOverlay? _overlay;

    internal IEnumerable<EntityUid> StainedEntities => _stainedEntities;

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NCBloodStainSurfaceOverlay(this, EntityManager);
        _overlays.AddOverlay(_overlay);
        SubscribeLocalEvent<NCBloodStainSurfaceComponent, ComponentStartup>(OnSurfaceStartup);
        SubscribeLocalEvent<NCBloodStainSurfaceComponent, ComponentShutdown>(OnSurfaceShutdown);
        SubscribeLocalEvent<NCBloodStainSurfaceComponent, AfterAutoHandleStateEvent>(OnSurfaceStateHandled);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        _overlay = null;
        _stainedEntities.Clear();
        _textures.Clear();

        base.Shutdown();
    }

    internal bool TryGetTexture(ProtoId<DecalPrototype> decalId, out Texture texture)
    {
        if (_textures.TryGetValue(decalId, out texture!))
            return true;

        if (!_prototypes.TryIndex(decalId, out DecalPrototype? decal))
            return false;

        texture = _sprites.Frame0(decal.Sprite);
        _textures.Add(decalId, texture);
        return true;
    }

    private void OnSurfaceStartup(Entity<NCBloodStainSurfaceComponent> surface, ref ComponentStartup args)
    {
        SynchronizeIndex(surface);
    }

    private void OnSurfaceShutdown(Entity<NCBloodStainSurfaceComponent> surface, ref ComponentShutdown args)
    {
        _stainedEntities.Remove(surface.Owner);
    }

    private void OnSurfaceStateHandled(
        Entity<NCBloodStainSurfaceComponent> surface,
        ref AfterAutoHandleStateEvent args)
    {
        SynchronizeIndex(surface);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _textures.Clear();
    }

    private void SynchronizeIndex(Entity<NCBloodStainSurfaceComponent> surface)
    {
        if (surface.Comp.Stains.Count > 0)
            _stainedEntities.Add(surface.Owner);
        else
            _stainedEntities.Remove(surface.Owner);
    }
}
