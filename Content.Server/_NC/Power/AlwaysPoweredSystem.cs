// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.Power;

/// <summary>
/// Applies map- or grid-scoped infinite power through the standard APC receiver state.
/// </summary>
public sealed partial class AlwaysPoweredSystem : EntitySystem
{
    [Dependency] private PowerReceiverSystem _power = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AlwaysPoweredComponent, ComponentStartup>(OnMarkerStartup);
        SubscribeLocalEvent<AlwaysPoweredComponent, ComponentRemove>(OnMarkerRemoved);
        // PowerNetSystem already owns ApcPowerReceiver + MapInit/ComponentRemove. ComponentStartup is a
        // distinct lifecycle event that runs after its ComponentInit allocation and before vanilla MapInit visuals.
        SubscribeLocalEvent<ApcPowerReceiverComponent, ComponentStartup>(OnReceiverStartup);
        SubscribeLocalEvent<ApcPowerReceiverComponent, EntParentChangedMessage>(OnReceiverParentChanged);
        SubscribeLocalEvent<MapGridComponent, EntParentChangedMessage>(OnGridParentChanged);
    }

    private void OnMarkerStartup(Entity<AlwaysPoweredComponent> marker, ref ComponentStartup args)
    {
        RefreshReceiversForMarker(marker.Owner);
    }

    private void OnMarkerRemoved(Entity<AlwaysPoweredComponent> marker, ref ComponentRemove args)
    {
        // ComponentRemove is raised while the marker can still be queried, so explicitly ignore it.
        RefreshReceiversForMarker(marker.Owner, marker.Owner);
    }

    private void OnReceiverStartup(Entity<ApcPowerReceiverComponent> receiver, ref ComponentStartup args)
    {
        RefreshReceiver(receiver);
    }

    private void OnReceiverParentChanged(Entity<ApcPowerReceiverComponent> receiver, ref EntParentChangedMessage args)
    {
        // Direct parent changes cover devices being moved between maps or grids after initialization.
        RefreshReceiver((receiver.Owner, receiver.Comp, args.Transform));
    }

    private void OnGridParentChanged(Entity<MapGridComponent> grid, ref EntParentChangedMessage args)
    {
        // Children keep the grid as their parent when a shuttle changes maps, so refresh that grid once.
        var query = EntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var receiver, out var xform))
        {
            if (xform.GridUid == grid.Owner)
                RefreshReceiver((uid, receiver, xform));
        }
    }

    private void RefreshReceiversForMarker(EntityUid marker, EntityUid? excludedMarker = null)
    {
        // Marker changes are rare. Query only power receivers and never scan entities every tick.
        var query = EntityQueryEnumerator<ApcPowerReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var receiver, out var xform))
        {
            if (uid != marker && xform.GridUid != marker && xform.MapUid != marker)
                continue;

            RefreshReceiver((uid, receiver, xform), excludedMarker);
        }
    }

    private void RefreshReceiver(
        Entity<ApcPowerReceiverComponent, TransformComponent?> receiver,
        EntityUid? excludedMarker = null)
    {
        if (!Resolve(receiver, ref receiver.Comp2))
            return;

        if (IsAlwaysPowered(receiver.Owner, receiver.Comp2, excludedMarker))
        {
            ForcePowered(receiver.Owner, receiver.Comp1);
            return;
        }

        RestorePowerState(receiver.Owner, receiver.Comp1);
    }

    private bool IsAlwaysPowered(EntityUid uid, TransformComponent xform, EntityUid? excludedMarker)
    {
        return HasActiveMarker(uid, excludedMarker)
            || HasActiveMarker(xform.GridUid, excludedMarker)
            || HasActiveMarker(xform.MapUid, excludedMarker);
    }

    private bool HasActiveMarker(EntityUid? uid, EntityUid? excludedMarker)
    {
        return uid is { } entity && entity != excludedMarker && HasComp<AlwaysPoweredComponent>(entity);
    }

    private void ForcePowered(EntityUid uid, ApcPowerReceiverComponent receiver)
    {
        if (!HasComp<AlwaysPoweredReceiverStateComponent>(uid) && (receiver.NeedsPower || receiver.PowerDisabled))
        {
            var state = EnsureComp<AlwaysPoweredReceiverStateComponent>(uid);
            state.NeedsPower = receiver.NeedsPower;
            state.PowerDisabled = receiver.PowerDisabled;
        }

        // Use the stock power API so networking and PowerChangedEvent remain authoritative.
        _power.SetNeedsPower(uid, false, receiver);
        _power.SetPowerDisabled(uid, false, receiver);
    }

    private void RestorePowerState(EntityUid uid, ApcPowerReceiverComponent receiver)
    {
        if (!TryComp<AlwaysPoweredReceiverStateComponent>(uid, out var original))
            return;

        _power.SetNeedsPower(uid, original.NeedsPower, receiver);
        _power.SetPowerDisabled(uid, original.PowerDisabled, receiver);
        RemCompDeferred<AlwaysPoweredReceiverStateComponent>(uid);
    }
}
