// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;

namespace Content.Shared._NC.Vehicle;

/// <summary>Restricts trunk operation to external users and accepts large cargo without capturing bystanders.</summary>
public sealed partial class NCVehicleTrunkSystem : EntitySystem
{
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedStorageSystem _storage = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleTrunkComponent, StorageOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<NCVehicleTrunkComponent, StorageCloseAttemptEvent>(OnCloseAttempt);
        SubscribeLocalEvent<NCVehicleTrunkComponent, EntityStorageInsertedIntoAttemptEvent>(OnInsertAttempt);
        SubscribeLocalEvent<NCVehicleTrunkComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
    }

    private void OnOpenAttempt(Entity<NCVehicleTrunkComponent> ent, ref StorageOpenAttemptEvent args)
    {
        if (!CanOperate(ent, args.User, out var reason))
        {
            args.Cancelled = true;
            if (!args.Silent && reason is { } message)
                _popup.PopupPredicted(Loc.GetString(message), ent, args.User);
        }
    }

    private void OnCloseAttempt(Entity<NCVehicleTrunkComponent> ent, ref StorageCloseAttemptEvent args)
    {
        if (args.User is not { } user || CanOperate(ent, user, out _))
            return;

        args.Cancelled = true;
    }

    private void OnInsertAttempt(Entity<NCVehicleTrunkComponent> ent,
        ref EntityStorageInsertedIntoAttemptEvent args)
    {
        // Vehicles cannot nest inside one another, and an active bystander cannot be swept up when the lid closes.
        if (HasComp<NCVehicleComponent>(args.ItemToInsert) ||
            !ent.Comp.AllowLivingMobs && TryComp<MobStateComponent>(args.ItemToInsert, out var mob) &&
            mob.CurrentState == MobState.Alive)
        {
            args.Cancelled = true;
        }
    }

    private void OnVerbs(Entity<NCVehicleTrunkComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract ||
            !TryComp<EntityStorageComponent>(ent, out var physical) || !physical.Open ||
            !TryComp<StorageComponent>(ent, out var inventory) ||
            !CanOperate(ent, args.User, out _))
        {
            return;
        }

        var user = args.User;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("nc-vehicle-trunk-inventory"),
            Act = () => _storage.OpenStorageUI(ent, user, inventory, false),
        });
    }

    public bool CanOperate(Entity<NCVehicleTrunkComponent> ent, EntityUid user, out LocId? reason)
    {
        reason = null;
        if (ent.Comp.ExternalOnly &&
            (TryComp<NCVehicleOccupantComponent>(user, out var occupant) && occupant.Vehicle == ent.Owner ||
             _containers.TryGetContainingContainer((user, null, null), out var containing) &&
             containing.Owner == ent.Owner))
        {
            reason = "nc-vehicle-trunk-external-only";
        }
        else if (!_access.CanOpen(ent))
        {
            reason = "nc-vehicle-trunk-locked";
        }
        else if (TryComp<PhysicsComponent>(ent, out var body) &&
                 body.LinearVelocity.Length() > ent.Comp.MaximumOpenSpeed)
        {
            reason = "nc-vehicle-trunk-moving";
        }
        else
        {
            return true;
        }

        return false;
    }
}
