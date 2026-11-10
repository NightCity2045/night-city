// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Blood bank. An empty bag is filled from a donor with a timed draw, the bag records the donor's real group,
/// and a filled bag spoils unless it is kept in a working cold storage. Compatibility is enforced by the
/// existing transfusion rules; this system only supplies the scarce physical resource.
/// </summary>
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

    private float _timer;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBloodBagComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCBloodBagComponent, NCBloodDrawDoAfterEvent>(OnDrawDone);
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
