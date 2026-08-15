// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Clickable;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Analyzers;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Wounds;

/// <summary>
/// Owns the always-on external-wound world overlay. Rendering is camera-bounded inside the overlay itself.
/// </summary>
public sealed partial class NCWorldWoundOverlaySystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resources = default!;
    [Dependency] private IClickMapManager _clickMaps = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    private readonly HashSet<EntityUid> _woundedEntities = [];
    private NCWorldWoundOverlay? _overlay;

    internal IEnumerable<EntityUid> WoundedEntities => _woundedEntities;

    /// <summary>
    /// Reports whether a replicated body is scheduled for wound rendering. This is intentionally read-only and is
    /// also used by the connected regression test to cover the client state-to-renderer boundary.
    /// </summary>
    public bool IsIndexed(EntityUid uid)
    {
        return _woundedEntities.Contains(uid);
    }

    /// <summary>
    /// World marks follow the same containment visibility boundary as the entity sprite. Diagnostic and targeting
    /// dolls do not use this method, so clinicians can still inspect wounds while the patient is inside a scanner.
    /// </summary>
    public bool ShouldRenderWorldWounds(EntityUid uid)
    {
        return TryComp(uid, out MetaDataComponent? meta) &&
               TryComp(uid, out TransformComponent? xform) &&
               !_containers.IsEntityOrParentInContainer(uid, meta, xform);
    }

    public override void Initialize()
    {
        base.Initialize();

        _overlay = new NCWorldWoundOverlay(this, EntityManager, _prototypes, _resources, _clickMaps);
        _overlays.AddOverlay(_overlay);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCVisibleWoundComponent, ComponentStartup>(OnWoundComponentStartup);
        SubscribeLocalEvent<NCVisibleWoundComponent, ComponentShutdown>(OnWoundComponentShutdown);
        SubscribeLocalEvent<NCVisibleWoundComponent, AfterAutoHandleStateEvent>(OnWoundStateHandled);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        _overlay = null;
        _woundedEntities.Clear();

        base.Shutdown();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _overlay?.ClearLayoutCache();
    }

    private void OnWoundComponentStartup(Entity<NCVisibleWoundComponent> entity, ref ComponentStartup args)
    {
        SynchronizeIndex(entity);
    }

    private void OnWoundComponentShutdown(Entity<NCVisibleWoundComponent> entity, ref ComponentShutdown args)
    {
        _woundedEntities.Remove(entity.Owner);
    }

    private void OnWoundStateHandled(Entity<NCVisibleWoundComponent> entity, ref AfterAutoHandleStateEvent args)
    {
        // Auto-generated state handling has already replaced Markers at this point.
        SynchronizeIndex(entity);
    }

    private void SynchronizeIndex(Entity<NCVisibleWoundComponent> entity)
    {
        if (entity.Comp.Markers.Count > 0)
            _woundedEntities.Add(entity.Owner);
        else
            _woundedEntities.Remove(entity.Owner);
    }
}
