// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Language;
using Content.Shared.Hands.Components;
using Content.Shared.Storage;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaDroneOperationsSystem
{
    /// <summary>Only the configured real gripper is used. No item spawning, forced pickup, or inventory replacement.</summary>
    public bool TryPickUp(EntityUid uid, EntityUid target, Func<bool> guard, EntityUid? actor = null)
    {
        if (!Exists(uid) || !Exists(target)) return false;
        if (!TryComp<NCMetaDroneManipulatorComponent>(uid, out var manipulator) || manipulator.Hand.Length is 0 or > 64 ||
            _containers.IsEntityOrParentInContainer(uid) || _containers.IsEntityOrParentInContainer(target) ||
            !_navigation.CanUseActuators(uid, actor) || !_devices.IsPowered(uid) || !guard() || !_interaction.InRangeUnobstructed(uid, target) ||
            _hands.GetHeldItem(uid, manipulator.Hand) != null) return false;
        return _hands.TryPickup(uid, target, manipulator.Hand, animate: false, commitGuard: () => guard() && _devices.IsPowered(uid) && _navigation.CanUseActuators(uid, actor)) &&
            _hands.GetHeldItem(uid, manipulator.Hand) == target;
    }
    public bool TryDropAt(EntityUid uid, EntityUid item, EntityUid marker, Func<bool> guard, EntityUid? actor = null)
    {
        if (!Exists(uid) || !Exists(item) || !Exists(marker)) return false;
        if (!TryComp<NCMetaDroneManipulatorComponent>(uid, out var manipulator) || !_devices.IsPowered(uid) ||
            _containers.IsEntityOrParentInContainer(uid) || _containers.IsEntityOrParentInContainer(marker) ||
            !_navigation.CanUseActuators(uid, actor) || _hands.GetHeldItem(uid, manipulator.Hand) != item || !Exists(marker) || !guard() ||
            Transform(marker).MapUid != Transform(uid).MapUid || !_interaction.InRangeUnobstructed(uid, marker, 1.25f)) return false;
        return _hands.TryDrop(uid, item, Transform(marker).Coordinates, commitGuard: () => guard() && _devices.IsPowered(uid) &&
            _navigation.CanUseActuators(uid, actor) && _hands.GetHeldItem(uid, manipulator.Hand) == item && _interaction.InRangeUnobstructed(uid, marker, 1.25f)) && !_hands.IsHolding(uid, item);
    }
    public bool TryStoreIn(EntityUid uid, EntityUid item, EntityUid target, Func<bool> guard, EntityUid? actor = null)
    {
        if (!Exists(uid) || !Exists(item) || !Exists(target)) return false;
        if (!TryComp<NCMetaDroneManipulatorComponent>(uid, out var manipulator) || !_devices.IsPowered(uid) ||
            _containers.IsEntityOrParentInContainer(uid) || _containers.IsEntityOrParentInContainer(target) ||
            !_navigation.CanUseActuators(uid, actor) || _hands.GetHeldItem(uid, manipulator.Hand) != item || !HasComp<StorageComponent>(target)) return false;
        return _storage.TryStoreHeld(target, uid, item, () => guard() && _devices.IsPowered(uid) && _navigation.CanUseActuators(uid, actor) && _hands.GetHeldItem(uid, manipulator.Hand) == item);
    }
    private static bool ValidWeapon(NCMetaDroneWeaponComponent weapon) => float.IsFinite(weapon.DegreesPerSecond) &&
        weapon.DegreesPerSecond is >= 1 and <= 180 && float.IsFinite(weapon.AimToleranceDegrees) && weapon.AimToleranceDegrees is >= 0.5f and <= 5;
    private void AdvanceAim(Job job, float frameTime)
    {
        var uid = job.Invocation.Device;
        if (!float.IsFinite(frameTime) || frameTime < 0 || !CanTarget(job) || !TryComp<NCMetaDroneWeaponComponent>(uid, out var weapon) || !ValidWeapon(weapon) ||
            !HasComp<BallisticAmmoProviderComponent>(uid) || !_blocker.CanChangeDirection(uid) ||
            !_guns.CanFireControlled(uid) || _guns.GetAmmoCount(uid) <= 0) { Cancel(job, "Interlock"); return; }
        var delta = _transform.GetWorldPosition(Transform(job.Target)) - _transform.GetWorldPosition(Transform(uid));
        if (!float.IsFinite(delta.LengthSquared()) || delta.LengthSquared() <= 0.01f) { Cancel(job, "OutOfRange"); return; }
        var goal = new Angle(Math.Atan2(delta.Y, delta.X));
        var tolerance = Angle.FromDegrees(weapon.AimToleranceDegrees);
        _rotation.TryRotateTo(uid, goal, Math.Clamp(frameTime, 0, 0.1f), tolerance, weapon.DegreesPerSecond * Math.PI / 180);
        // The native method can return true when an event blocks rotation. Read the actual authoritative facing.
        if (Math.Abs(Angle.ShortestDistance(_transform.GetWorldRotation(Transform(uid)), goal).Theta) > tolerance.Theta) return;
        var beforeAmmo = _guns.GetAmmoCount(uid);
        job.OutputCommitting = true;
        try
        {
            _guns.TryFireControlled(uid, uid, Transform(job.Target).Coordinates, job.Target, () => CanTarget(job) && IsFacingCurrentTarget(uid, job.Target));
            var spent = _guns.GetAmmoCount(uid) < beforeAmmo;
            job.Completion.TrySetResult(job.ShotConfirmed ? new(MetaOperationStatus.Applied, 1) :
                spent ? new(MetaOperationStatus.OutcomeUnknown, 0, "OutcomeUnknown") : new(MetaOperationStatus.Rejected, 0, "ActionRejected"));
        }
        finally { job.OutputCommitting = false; job.Aiming = false; }
    }
    private bool IsFacingCurrentTarget(EntityUid uid, EntityUid target)
    {
        if (!TryComp<NCMetaDroneWeaponComponent>(uid, out var weapon) || !ValidWeapon(weapon) || !_blocker.CanAttack(uid) || !_blocker.CanChangeDirection(uid)) return false;
        var delta = _transform.GetWorldPosition(Transform(target)) - _transform.GetWorldPosition(Transform(uid));
        return float.IsFinite(delta.LengthSquared()) && delta.LengthSquared() > 0.01f &&
            Math.Abs(Angle.ShortestDistance(_transform.GetWorldRotation(Transform(uid)), new Angle(Math.Atan2(delta.Y, delta.X))).Theta) <= weapon.AimToleranceDegrees * Math.PI / 180;
    }
    private void OnShot(Entity<NCMetaDroneWeaponComponent> ent, ref AmmoShotEvent args)
    {
        if (args.FiredProjectiles.Count == 0) return;
        var job = _jobs.Values.FirstOrDefault(candidate => candidate.Invocation.Device == ent.Owner && candidate.OutputCommitting && candidate.Kind == "weapon");
        if (job != null) job.ShotConfirmed = true;
    }
}
