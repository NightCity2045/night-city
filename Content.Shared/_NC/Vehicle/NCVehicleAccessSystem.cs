// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Lock;
using Content.Shared.Storage;
using Robust.Shared.Physics.Components;

namespace Content.Shared._NC.Vehicle;

/// <summary>Shared authorization keeps predicted ignition consistent with physical locks and keys.</summary>
public sealed class NCVehicleAccessSystem : EntitySystem
{
    [Dependency] private SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleAccessComponent, LockToggleAttemptEvent>(OnLock);
        SubscribeLocalEvent<NCVehicleAccessComponent, StorageInteractAttemptEvent>(OnStorage);
    }

    public bool HasKey(EntityUid vehicle, EntityUid user)
    {
        if (!TryComp<NCVehicleAccessComponent>(vehicle, out var access) || !access.Registered)
            return false;
        foreach (var held in _hands.EnumerateHeld(user))
            if (TryComp<NCVehicleKeyComponent>(held, out var key) && key.Vehicle == vehicle &&
                key.Generation == access.KeyGeneration)
                return true;
        return false;
    }

    public bool CanIgnite(EntityUid vehicle, EntityUid user)
        => (!TryComp<NCVehicleRecoveryComponent>(vehicle, out var recovery) || recovery.Cargo == null || recovery.Secured) &&
            (!TryComp<NCVehicleAccessComponent>(vehicle, out var access) || !access.Registered ||
                access.Hotwired || HasKey(vehicle, user));

    public bool CanOpen(EntityUid vehicle)
        => !TryComp<LockComponent>(vehicle, out var vehicleLock) || !vehicleLock.Locked;

    private void OnLock(Entity<NCVehicleAccessComponent> ent, ref LockToggleAttemptEvent args)
        => args.Cancelled |= !HasKey(ent, args.User);

    private void OnStorage(Entity<NCVehicleAccessComponent> ent, ref StorageInteractAttemptEvent args)
    {
        // The stock storage system enforces this for opening, inserting and removing trunk contents.
        args.Cancelled |= !CanOpen(ent) || !TryComp<PhysicsComponent>(ent, out var body) ||
            body.LinearVelocity.Length() > ent.Comp.MaximumServiceSpeed;
    }
}
