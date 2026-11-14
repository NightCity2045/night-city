// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Physical disposal of hazardous waste. Machines fill a tank, the tank is emptied into a canister, and the
/// canister goes to a powered incinerator. A canister left lying around leaks toxic fumes: the consequence
/// has a cause, and the cause is that nobody carried the waste out.
/// </summary>
public sealed partial class NCPharmaWasteSystem : EntitySystem
{
    private static readonly EntProtoId SmokeEntity = "Smoke";
    private static readonly ProtoId<Content.Shared.Chemistry.Reagent.ReagentPrototype> Fumes = "NCReactorFumes";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SmokeSystem _smoke = default!;

    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaWasteTankComponent, InteractUsingEvent>(OnTankUsing);
        SubscribeLocalEvent<NCPharmaWasteBinComponent, InteractUsingEvent>(OnBinUsing);
    }

    /// <summary>True when the machine can take this much more waste. A machine without a tank always can.</summary>
    public bool CanTake(EntityUid machine, float amount)
    {
        return !TryComp<NCPharmaWasteTankComponent>(machine, out var tank) || tank.Level + amount <= tank.Capacity;
    }

    public void Add(EntityUid machine, float amount)
    {
        if (TryComp<NCPharmaWasteTankComponent>(machine, out var tank))
            tank.Level = MathF.Min(tank.Capacity, tank.Level + MathF.Max(0f, amount));
    }

    private void OnTankUsing(Entity<NCPharmaWasteTankComponent> tank, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCPharmaWasteCanisterComponent>(args.Used, out var canister) ||
            canister.Filled || canister.FilledPrototype is not { } filledId)
        {
            return;
        }

        args.Handled = true;
        if (tank.Comp.Level <= 0f)
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-nothing"), tank, args.User);
            return;
        }

        var filled = Spawn(filledId, Transform(args.User).Coordinates);
        if (TryComp<NCPharmaWasteCanisterComponent>(filled, out var filledComponent))
        {
            filledComponent.Filled = true;
            filledComponent.Age = TimeSpan.Zero;
        }

        QueueDel(args.Used);
        tank.Comp.Level = 0f;
        _hands.TryPickupAnyHand(args.User, filled);
        _popup.PopupEntity(Loc.GetString("nc-pharma-waste-emptied"), tank, args.User);
    }

    private void OnBinUsing(Entity<NCPharmaWasteBinComponent> bin, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCPharmaWasteCanisterComponent>(args.Used, out var canister) || !canister.Filled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(bin.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), bin, args.User);
            return;
        }

        var empty = canister.EmptyPrototype is { } emptyId ? Spawn(emptyId, Transform(args.User).Coordinates) : (EntityUid?) null;
        QueueDel(args.Used);
        if (empty != null)
            _hands.TryPickupAnyHand(args.User, empty.Value);
        _popup.PopupEntity(Loc.GetString("nc-pharma-waste-disposed"), bin, args.User);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + CheckInterval;
        var query = EntityQueryEnumerator<NCPharmaWasteCanisterComponent>();
        while (query.MoveNext(out var uid, out var canister))
        {
            if (!canister.Filled)
                continue;

            canister.Age += CheckInterval;
            if (canister.Age < canister.LeakAfter)
                continue;

            canister.Age = TimeSpan.Zero;
            var solution = new Solution();
            solution.AddReagent(Fumes, FixedPoint2.New(8));
            var smoke = Spawn(SmokeEntity, Transform(uid).Coordinates);
            _smoke.StartSmoke(smoke, solution, 10f, 4);
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-leak"), uid, PopupType.MediumCaution);
        }
    }
}
