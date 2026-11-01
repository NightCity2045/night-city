// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;

namespace Content.Shared.Weapons.Ranged.Systems;

public abstract partial class SharedGunSystem
{
    private readonly Dictionary<EntityUid, Func<bool>> _controlledShotGuards = [];
    public bool HasPendingControlledShot(Entity<GunComponent?> gun) => Resolve(gun, ref gun.Comp, false) && gun.Comp.ShootCoordinates != null;
    public bool CanFireControlled(Entity<GunComponent?> gun) => Resolve(gun, ref gun.Comp, false) &&
        gun.Comp.SelectedMode == SelectiveFire.SemiAuto && !gun.Comp.BurstActivated &&
        float.IsFinite(gun.Comp.FireRateModified) && gun.Comp.FireRateModified is > 0 and <= 100 && CanShoot(gun.Comp);
    protected bool CanCommitControlledShot(EntityUid uid) => !_controlledShotGuards.TryGetValue(uid, out var guard) || guard();

    /// <summary>One real semi-automatic trigger. Native ammo, cooldown, attempt events, recoil and trajectory remain authoritative.</summary>
    public bool TryFireControlled(EntityUid user, Entity<GunComponent?> gun, EntityCoordinates aim, EntityUid target, Func<bool> guard)
    {
        if (!Resolve(gun, ref gun.Comp, false) || !CanFireControlled(gun) || _controlledShotGuards.Count >= 64 ||
            _controlledShotGuards.ContainsKey(gun.Owner) || !guard()) return false;
        _controlledShotGuards.Add(gun.Owner, guard);
        try { return AttemptShoot(user, (gun.Owner, gun.Comp), aim, target); }
        finally
        {
            // This capability never leaves a background native burst/autofire target after its one trigger.
            StopShooting((gun.Owner, gun.Comp));
            gun.Comp.ShootCoordinates = null;
            gun.Comp.Target = null;
            _controlledShotGuards.Remove(gun.Owner);
        }
    }
}
