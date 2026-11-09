// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.CombatMode;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Slows a shooter who is in combat mode, holds a firearm and has deliberately selected a body zone.
/// The slowdown is a hard speed cap, so speed boosts from other systems cannot lift it.
/// </summary>
public sealed class NCAimedMovementSystem : EntitySystem
{
    [Dependency] private SharedCombatModeSystem _combat = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private ProtoId<NCTargetingAccuracyProfilePrototype>? _defaultProfile;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCAimedTargetingComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        SubscribeLocalEvent<NCAimedTargetingComponent, AfterAutoHandleStateEvent>(OnAfterState);
        SubscribeLocalEvent<NCAimedTargetingComponent, NCCombatModeChangedEvent>(OnCombatModeChanged);
        SubscribeLocalEvent<NCAimedTargetingComponent, DidEquipHandEvent>(OnEquipped);
        SubscribeLocalEvent<NCAimedTargetingComponent, DidUnequipHandEvent>(OnUnequipped);
        SubscribeLocalEvent<GunComponent, HandSelectedEvent>(OnGunSelected);
        SubscribeLocalEvent<GunComponent, HandDeselectedEvent>(OnGunDeselected);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    /// <summary>
    /// Updates the replicated flag after the server accepted a new targeting selection.
    /// </summary>
    public void SetZoneSelected(EntityUid user, bool selected)
    {
        var aimed = EnsureComp<NCAimedTargetingComponent>(user);
        if (aimed.ZoneSelected == selected)
            return;

        aimed.ZoneSelected = selected;
        Dirty(user, aimed);
        _movement.RefreshMovementSpeedModifiers(user);
    }

    /// <summary>
    /// True while the entity is in combat mode, holds a firearm in its active hand and has a zone selected.
    /// </summary>
    public bool IsAimed(Entity<NCAimedTargetingComponent?> user)
    {
        return Resolve(user, ref user.Comp, false) &&
               user.Comp.ZoneSelected &&
               _combat.IsInCombatMode(user.Owner) &&
               _hands.GetActiveItem(user.Owner) is { } held &&
               HasComp<GunComponent>(held);
    }

    private void OnRefreshSpeed(Entity<NCAimedTargetingComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!IsAimed((ent, ent.Comp)) ||
            !TryGetProfile(out var profile) ||
            !TryComp<MovementSpeedModifierComponent>(ent, out var movement))
        {
            return;
        }

        var multiplier = float.IsFinite(profile.AimedMovementSpeedMultiplier)
            ? Math.Clamp(profile.AimedMovementSpeedMultiplier, 0.01f, 1f)
            : 1f;
        args.LimitSpeed(movement.BaseWalkSpeed * multiplier, movement.BaseSprintSpeed * multiplier);
    }

    private void OnAfterState(Entity<NCAimedTargetingComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnCombatModeChanged(Entity<NCAimedTargetingComponent> ent, ref NCCombatModeChangedEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnEquipped(Entity<NCAimedTargetingComponent> ent, DidEquipHandEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnUnequipped(Entity<NCAimedTargetingComponent> ent, DidUnequipHandEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent.Owner);
    }

    private void OnGunSelected(Entity<GunComponent> gun, HandSelectedEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(args.User);
    }

    private void OnGunDeselected(Entity<GunComponent> gun, HandDeselectedEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(args.User);
    }

    private bool TryGetProfile(out NCTargetingAccuracyProfilePrototype profile)
    {
        if (_defaultProfile is { } cached && _prototypes.TryIndex(cached, out profile!))
            return true;

        var defaults = _prototypes.EnumeratePrototypes<NCTargetingAccuracyProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();
        _defaultProfile = defaults.Length == 1 ? defaults[0].ID : null;

        if (_defaultProfile is { } resolved && _prototypes.TryIndex(resolved, out profile!))
            return true;

        profile = default!;
        return false;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _defaultProfile = null;
    }
}
