// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.DoAfter;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Blood bank. An empty bag is filled from a donor with a timed draw, the bag records the donor's real group,
/// and a filled bag spoils unless it is kept in a working cold storage. Compatibility is enforced by the
/// existing transfusion rules; this system only supplies the scarce physical resource.
/// </summary>
/// <summary>Splits donated whole blood into red cells and plasma.</summary>
[RegisterComponent]
public sealed partial class NCBloodCentrifugeComponent : Component
{
    [DataField]
    public string Slot = "bag";

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(15);

    [DataField]
    public EntProtoId RedCellsPrototype = "NCBloodBagRedCells";

    [DataField]
    public EntProtoId PlasmaPrototype = "NCBloodBagPlasma";
}

public sealed class NCBloodBankSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;
    private const float SpoilageInterval = 30f;

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private NCCirculationBloodSystem _blood = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;

    private float _timer;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBloodBagComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCBloodBagComponent, NCBloodDrawDoAfterEvent>(OnDrawDone);
        SubscribeLocalEvent<NCBloodCentrifugeComponent, ActivateInWorldEvent>(OnCentrifugeActivate);
        SubscribeLocalEvent<NCBloodCentrifugeComponent, NCBloodCentrifugeDoAfterEvent>(OnCentrifugeDone);
    }

    private void OnAfterInteract(Entity<NCBloodBagComponent> bag, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || bag.Comp.Filled || bag.Comp.FilledPrototype == null ||
            args.Target is not { } donor || !HasComp<NCMedicalBodyComponent>(donor))
        {
            return;
        }

        args.Handled = true;
        var doAfter = new DoAfterArgs(EntityManager, args.User, TimeSpan.FromSeconds(8),
            new NCBloodDrawDoAfterEvent(), bag.Owner, target: donor, used: bag.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = InteractionRange,
        };
        _doAfter.TryStartDoAfter(doAfter);
    }

    private void OnDrawDone(Entity<NCBloodBagComponent> bag, ref NCBloodDrawDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } donor || bag.Comp.FilledPrototype is not { } filledId)
            return;

        args.Handled = true;
        if (!_interaction.InRangeUnobstructed(args.User, donor, InteractionRange) ||
            !_blood.TryWithdraw(donor, bag.Comp.Volume, bag.Comp.DonorFloorFraction, out var drawn, out var group))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-draw-refused"), donor, args.User);
            return;
        }

        var filled = Spawn(filledId, Transform(args.User).Coordinates);
        var component = EnsureComp<NCBloodBagComponent>(filled);
        component.Filled = true;
        component.Group = group;
        component.Donor = Name(donor);
        component.Volume = drawn;
        Dirty(filled, component);
        QueueDel(bag.Owner);
        _hands.TryPickupAnyHand(args.User, filled);
        _popup.PopupEntity(Loc.GetString("nc-lab-draw-done"), donor, args.User);
    }

    private EntityUid? GetWholeBag(Entity<NCBloodCentrifugeComponent> centrifuge)
    {
        return _slots.TryGetSlot(centrifuge.Owner, centrifuge.Comp.Slot, out var slot) &&
               slot.Item is { } item && TryComp<NCBloodBagComponent>(item, out var bag) &&
               bag.Filled && !bag.Spoiled && bag.Component == NCBloodComponent.Whole
            ? item
            : null;
    }

    private void OnCentrifugeActivate(Entity<NCBloodCentrifugeComponent> centrifuge, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        if (!_power.IsPowered(centrifuge.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), centrifuge, args.User);
            return;
        }

        if (GetWholeBag(centrifuge) == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-centrifuge-needs-whole"), centrifuge, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, args.User, centrifuge.Comp.Duration,
            new NCBloodCentrifugeDoAfterEvent(), centrifuge.Owner, target: centrifuge.Owner)
        {
            BreakOnMove = true,
            DistanceThreshold = InteractionRange,
        };
        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-started"), centrifuge, args.User);
    }

    /// <summary>Splits a whole-blood bag into a bag of red cells and a bag of plasma, keeping group and donor.</summary>
    private void OnCentrifugeDone(Entity<NCBloodCentrifugeComponent> centrifuge, ref NCBloodCentrifugeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(centrifuge.Owner) || GetWholeBag(centrifuge) is not { } whole ||
            !TryComp<NCBloodBagComponent>(whole, out var source))
        {
            return;
        }

        var coordinates = Transform(centrifuge).Coordinates;
        Split(centrifuge.Comp.RedCellsPrototype, NCBloodComponent.RedCells, source, coordinates, args.User);
        Split(centrifuge.Comp.PlasmaPrototype, NCBloodComponent.Plasma, source, coordinates, args.User);
        QueueDel(whole);
        _popup.PopupEntity(Loc.GetString("nc-lab-centrifuge-done"), centrifuge, args.User);
    }

    private void Split(EntProtoId prototype, NCBloodComponent kind, NCBloodBagComponent source, EntityCoordinates coordinates, EntityUid user)
    {
        var bag = Spawn(prototype, coordinates);
        var component = EnsureComp<NCBloodBagComponent>(bag);
        component.Filled = true;
        component.Component = kind;
        component.Group = source.Group;
        component.Donor = source.Donor;
        component.Volume = kind == NCBloodComponent.RedCells ? source.Volume * 0.55f : source.Volume * 0.45f;
        component.ExposedSeconds = source.ExposedSeconds;
        Dirty(bag, component);
        _hands.TryPickupAnyHand(user, bag);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _timer += frameTime;
        if (_timer < SpoilageInterval)
            return;

        var seconds = _timer;
        _timer = 0f;
        var query = EntityQueryEnumerator<NCBloodBagComponent>();
        while (query.MoveNext(out var uid, out var bag))
        {
            if (!bag.Filled || bag.Spoiled || _batches.IsInColdStorage(uid))
                continue;

            bag.ExposedSeconds += seconds;
            if (bag.ExposedSeconds < bag.ShelfLifeSeconds)
                continue;

            bag.Spoiled = true;
            Dirty(uid, bag);
        }
    }
}
