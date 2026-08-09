// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Server.Doors.Systems;
using Content.Server.Popups;
using Content.Shared._NC.Housing.Components;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;

namespace Content.Server._NC.Housing;

/// <summary>
/// Implements property doors without extending SS14's static access-level prototype set.
/// A short-lived in-memory allowance lets only the validated swipe pass the normal door event chain.
/// </summary>
public sealed partial class NCPropertyKeyCardSystem : EntitySystem
{
    [Dependency] private DoorSystem _doors = default!;
    [Dependency] private PopupSystem _popup = default!;

    private readonly HashSet<(EntityUid Door, EntityUid User)> _authorizedSwipes = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPropertyLockComponent, BeforeDoorOpenedEvent>(OnBeforeDoorOpened);
        SubscribeLocalEvent<NCPropertyLockComponent, AfterInteractUsingEvent>(OnAfterInteractUsing);
    }

    private void OnBeforeDoorOpened(
        Entity<NCPropertyLockComponent> entity,
        ref BeforeDoorOpenedEvent args)
    {
        if (!entity.Comp.Enabled)
            return;
        if (args.User is not { } user || !_authorizedSwipes.Contains((entity.Owner, user)))
            args.Cancel();
    }

    private void OnAfterInteractUsing(
        Entity<NCPropertyLockComponent> entity,
        ref AfterInteractUsingEvent args)
    {
        if (args.Handled || !args.CanReach || !TryComp(args.Used, out NCPropertyKeyCardComponent? card))
            return;

        args.Handled = true;
        if (!entity.Comp.Enabled || card.PropertyId != entity.Comp.PropertyId ||
            card.AccessRevision != entity.Comp.AccessRevision ||
            !card.Permissions.HasFlag(entity.Comp.RequiredPermission) ||
            card.ValidUntil is { } expiry && expiry <= DateTime.UtcNow)
        {
            _popup.PopupEntity(Loc.GetString("nc-property-card-denied"), entity.Owner, args.User);
            return;
        }

        var allowance = (entity.Owner, args.User);
        _authorizedSwipes.Add(allowance);
        try
        {
            if (!_doors.TryOpen(entity.Owner, user: args.User))
            {
                _popup.PopupEntity(Loc.GetString("nc-property-door-blocked"), entity.Owner, args.User);
                return;
            }

            _popup.PopupEntity(Loc.GetString("nc-property-card-accepted"), entity.Owner, args.User);
        }
        finally
        {
            // The capability exists only for this synchronous TryOpen event chain and cannot authorize a later bump.
            _authorizedSwipes.Remove(allowance);
        }
    }
}
