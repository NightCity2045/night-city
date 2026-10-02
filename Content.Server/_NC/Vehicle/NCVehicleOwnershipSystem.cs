// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Stack;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Lock;
using Content.Shared.Stacks;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Physical handovers and interruptible theft, with no durable registration or bank writes.</summary>
public sealed partial class NCVehicleOwnershipSystem : EntitySystem
{
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private LockSystem _locks = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleAccessComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<NCVehicleAccessComponent, NCVehicleOwnershipMessage>(OnMessage);
        SubscribeLocalEvent<NCVehicleAccessComponent, NCVehicleTheftDoAfterEvent>(OnTheftComplete);
    }

    private void OnVerbs(Entity<NCVehicleAccessComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        if (!ent.Comp.Registered)
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-vehicle-register"), Act = () => TryRegister(ent, user) });
        else
        {
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-vehicle-ownership"), Act = () => TryOpen(ent, user) });
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-vehicle-break-lock"), Act = () => TryStartTheft(ent, user, false) });
            args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-vehicle-hotwire"), Act = () => TryStartTheft(ent, user, true) });
        }
    }

    public bool CanInteract(EntityUid uid, EntityUid user)
        => !TerminatingOrDeleted(uid) && !TerminatingOrDeleted(user) && HasComp<ActorComponent>(user) &&
            _blocker.CanConsciouslyPerformAction(user) && !HasComp<NCVehicleOccupantComponent>(user) &&
            TryComp<NCVehicleAccessComponent>(uid, out var access) &&
            _interaction.InRangeUnobstructed(user, uid, range: access.InteractionRange) &&
            TryComp<NCVehicleDriveComponent>(uid, out var drive) && !drive.EngineRunning && !drive.Cranking &&
            TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.Length() <= access.MaximumServiceSpeed;

    private bool IsEmpty(EntityUid uid)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis))
            return false;
        foreach (var seat in chassis.Seats.Keys)
            if (_containers.TryGetContainer(uid, "nc-vehicle-" + seat, out var container) && container.ContainedEntities.Count != 0)
                return false;
        return true;
    }

    public bool TryRegister(EntityUid uid, EntityUid user)
    {
        if (!CanInteract(uid, user) || !IsEmpty(uid) || !TryComp<NCVehicleAccessComponent>(uid, out var access) || access.Registered)
            return false;
        return TryChangeOwner(uid, user);
    }

    /// <summary>
    /// Registers a freshly delivered dealership vehicle without requiring the buyer to stand inside the delivery bay.
    /// The caller must own the authoritative purchase and pass a newly spawned, empty vehicle.
    /// </summary>
    public bool TryRegisterPurchasedVehicle(EntityUid uid, EntityUid user)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(user) || !HasComp<ActorComponent>(user) ||
            !IsEmpty(uid) || !TryComp<NCVehicleAccessComponent>(uid, out var access) || access.Registered)
            return false;
        return TryChangeOwner(uid, user);
    }

    private bool TryChangeOwner(EntityUid uid, EntityUid user)
    {
        var access = Comp<NCVehicleAccessComponent>(uid);
        if (!_prototypes.HasIndex(access.KeyPrototype))
            return false;
        // Prepare the physical key before revoking the old generation.
        var keyUid = Spawn(access.KeyPrototype, Transform(user).Coordinates);
        CompleteChangeOwner(uid, user, keyUid);
        return true;
    }

    private void CompleteChangeOwner(EntityUid uid, EntityUid user, EntityUid keyUid)
    {
        var access = Comp<NCVehicleAccessComponent>(uid);
        var key = EnsureComp<NCVehicleKeyComponent>(keyUid);
        access.KeyGeneration++;
        access.SaleRevision++;
        access.Registered = true;
        access.Owner = user;
        access.SalePrice = -1;
        access.Hotwired = false;
        key.Vehicle = uid;
        key.Generation = access.KeyGeneration;
        Dirty(keyUid, key);
        Dirty(uid, access);
        _locks.Unlock(uid, user);
        _hands.TryPickupAnyHand(user, keyUid);
        Refresh(uid);
    }

    public bool TryOpen(EntityUid uid, EntityUid user)
    {
        if (!CanInteract(uid, user))
            return false;
        Refresh(uid);
        return _ui.TryOpenUi(uid, NCVehicleUiKey.Ownership, user);
    }

    private void OnMessage(Entity<NCVehicleAccessComponent> ent, ref NCVehicleOwnershipMessage args)
    {
        if (args.Action == NCVehicleOwnershipAction.Accept)
            TryBuy(ent, args.Actor, args.Price, args.Revision);
        else if (args.Action == NCVehicleOwnershipAction.Rekey)
            TryRekey(ent, args.Actor);
        else
            TryOffer(ent, args.Actor, args.Action == NCVehicleOwnershipAction.Withdraw ? -1 : args.Price);
    }

    public bool TryRekey(EntityUid uid, EntityUid user)
    {
        if (!CanInteract(uid, user) || !IsEmpty(uid) || Comp<NCVehicleAccessComponent>(uid).Owner != user || !_access.HasKey(uid, user))
            return false;
        return TryChangeOwner(uid, user);
    }

    public bool TryOffer(EntityUid uid, EntityUid user, int price)
    {
        if (!CanInteract(uid, user) || !IsEmpty(uid) || !TryComp<NCVehicleAccessComponent>(uid, out var access) ||
            access.Owner != user || !_access.HasKey(uid, user) || price < -1 || price > access.MaximumSalePrice)
            return false;
        access.SalePrice = price;
        access.SaleRevision++;
        Dirty(uid, access);
        Refresh(uid);
        return true;
    }

    public bool TryBuy(EntityUid uid, EntityUid buyer, int expectedPrice, uint revision)
    {
        if (!CanInteract(uid, buyer) || !IsEmpty(uid) || !TryComp<NCVehicleAccessComponent>(uid, out var access) ||
            access.Owner is not { } seller || seller == buyer || !CanInteract(uid, seller) ||
            !_access.HasKey(uid, seller) || access.SalePrice < 0 || access.SalePrice != expectedPrice ||
            access.SaleRevision != revision || !_prototypes.HasIndex(access.KeyPrototype))
            return false;
        var price = access.SalePrice;
        EntityUid? preparedKey = null;
        if (price > 0)
        {
            var cash = _hands.GetActiveItem(buyer);
            if (cash == null || !TryComp<StackComponent>(cash, out var stack) || stack.Unlimited ||
                stack.StackTypeId != access.Currency || stack.Count < price ||
                !_prototypes.TryIndex(access.Currency, out var currency) || !_prototypes.HasIndex(currency.Spawn))
                return false;
            // Prepare the only fallible ownership resource before withdrawing physical cash.
            preparedKey = Spawn(access.KeyPrototype, Transform(buyer).Coordinates);
            var paid = _stacks.Split((cash.Value, stack), price, Transform(seller).Coordinates, buyer);
            if (paid == null)
            {
                Del(preparedKey.Value);
                return false;
            }
            _hands.TryPickupAnyHand(seller, paid.Value);
        }
        if (preparedKey is { } key)
        {
            CompleteChangeOwner(uid, buyer, key);
            return true;
        }
        return TryChangeOwner(uid, buyer);
    }

    private bool CanSteal(EntityUid uid, EntityUid user, EntityUid tool, bool hotwire)
    {
        if (!CanInteract(uid, user) || _hands.GetActiveItem(user) != tool || !TryComp<NCVehicleAccessComponent>(uid, out var access) ||
            !access.Registered || !_tools.HasQuality(tool, hotwire ? access.HotwireTool : access.BreakLockTool))
            return false;
        if (!hotwire)
            return !_access.CanOpen(uid);
        return _access.CanOpen(uid) && !access.Hotwired && TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) &&
            assembly.HoodOpen && TryComp<NCVehicleDriveComponent>(uid, out var drive) &&
            NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition) > 0;
    }

    public bool TryStartTheft(EntityUid uid, EntityUid user, bool hotwire)
    {
        if (_hands.GetActiveItem(user) is not { } tool || !CanSteal(uid, user, tool, hotwire))
            return false;
        var access = Comp<NCVehicleAccessComponent>(uid);
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, hotwire ? access.HotwireSeconds : access.BreakLockSeconds,
            new NCVehicleTheftDoAfterEvent { Hotwire = hotwire, Generation = access.KeyGeneration }, uid, uid, tool)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true });
    }

    private void OnTheftComplete(Entity<NCVehicleAccessComponent> ent, ref NCVehicleTheftDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Generation != ent.Comp.KeyGeneration || args.Used is not { } tool ||
            !CanSteal(ent, args.User, tool, args.Hotwire))
            return;
        args.Handled = true;
        if (args.Hotwire)
        {
            ent.Comp.Hotwired = true;
            Dirty(ent);
        }
        else
            _locks.Unlock(ent, args.User);
    }

    private void Refresh(EntityUid uid)
    {
        var access = Comp<NCVehicleAccessComponent>(uid);
        var owner = access.Owner is { } entity && !TerminatingOrDeleted(entity) ? MetaData(entity).EntityName : Loc.GetString("nc-vehicle-owner-unavailable");
        _ui.SetUiState(uid, NCVehicleUiKey.Ownership, new NCVehicleOwnershipState(owner, access.SalePrice, access.SaleRevision));
    }
}
