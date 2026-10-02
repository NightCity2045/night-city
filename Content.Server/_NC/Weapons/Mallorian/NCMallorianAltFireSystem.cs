// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._NC.Weapons.Mallorian;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._NC.Weapons.Mallorian;

/// <summary>
/// Resolves the Mallorian's targeted alternative fire through the stock ammo and projectile pipelines.
/// The server consumes every available cartridge atomically and authoritatively fires one spread volley.
/// </summary>
public sealed partial class NCMallorianAltFireSystem : EntitySystem
{
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private GunSystem _gun = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMallorianAltFireComponent, NCMallorianAltFireActionEvent>(OnAltFire);
    }

    private void OnAltFire(Entity<NCMallorianAltFireComponent> weapon, ref NCMallorianAltFireActionEvent args)
    {
        if (args.Handled || !TryComp<GunComponent>(weapon, out var gun))
            return;

        var user = args.Performer;

        // Item actions can remain visible for a frame during hand changes, so authority is revalidated here.
        if (_hands.GetActiveItem(user) != weapon.Owner || !_actionBlocker.CanAttack(user) || !_gun.CanShoot(gun))
            return;

        var fromCoordinates = Transform(user).Coordinates;
        var fromMap = _transform.ToMapCoordinates(fromCoordinates);
        var targetMap = _transform.ToMapCoordinates(args.Target);
        if (fromMap.MapId != targetMap.MapId || (targetMap.Position - fromMap.Position).IsLengthZero())
            return;

        var attempted = new AttemptShootEvent(user, null);
        RaiseLocalEvent(weapon.Owner, ref attempted);
        if (attempted.Cancelled)
        {
            if (attempted.Message != null)
                _popup.PopupClient(attempted.Message, weapon.Owner, user);
            return;
        }

        RaiseLocalEvent(user, ref attempted);
        if (attempted.Cancelled)
            return;

        var available = _gun.GetAmmoCount(weapon.Owner);
        if (available <= 0)
        {
            _popup.PopupCursor(Loc.GetString("gun-magazine-fired-empty"), user);
            _audio.PlayPredicted(gun.SoundEmpty, weapon.Owner, user);
            args.Handled = true;
            return;
        }

        // The provider owns chamber/magazine ordering and appearance updates; request every remaining round once.
        var ammoEvent = new TakeAmmoEvent(available, [], fromCoordinates, user);
        RaiseLocalEvent(weapon.Owner, ammoEvent);
        if (ammoEvent.Ammo.Count == 0)
        {
            _audio.PlayPredicted(gun.SoundEmpty, weapon.Owner, user);
            args.Handled = true;
            return;
        }

        var useDelay = args.Action.Comp.UseDelay ?? TimeSpan.FromSeconds(1);
        _gun.NCPrepareTargetedShot(
            (weapon.Owner, gun),
            args.Target,
            args.Entity,
            _timing.CurTime + useDelay);

        // The flag is synchronous context for GunSystem: spread all rounds but emit only one sound and flash.
        weapon.Comp.VolleyActive = true;
        try
        {
            _gun.Shoot((weapon.Owner, gun), ammoEvent.Ammo, fromCoordinates, args.Target, out var userImpulse, user, attempted.ThrowItems);

            var shotEvent = new GunShotEvent(user, ammoEvent.Ammo);
            RaiseLocalEvent(weapon.Owner, ref shotEvent);

            if (userImpulse && TryComp<PhysicsComponent>(user, out var body))
                _gun.CauseImpulse(fromCoordinates, args.Target, (user, body));
        }
        finally
        {
            weapon.Comp.VolleyActive = false;
        }

        args.Handled = true;
    }
}
