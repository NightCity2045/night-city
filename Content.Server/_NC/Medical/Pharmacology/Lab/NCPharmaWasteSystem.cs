// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// Physical disposal of hazardous waste. A machine pours its waste, as a reagent, into the canister in its waste
/// slot; a full canister is carried to a powered incinerator, which burns the waste and leaves the canister. A canister
/// left lying around with waste in it leaks toxic fumes: the consequence has a cause, and the cause is that nobody
/// carried the waste out.
/// </summary>
public sealed partial class NCPharmaWasteSystem : EntitySystem
{
    private static readonly EntProtoId SmokeEntity = "Smoke";
    private static readonly ProtoId<ReagentPrototype> Fumes = "NCReactorFumes";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SmokeSystem _smoke = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaWasteBinComponent, InteractUsingEvent>(OnBinUsing);
    }

    /// <summary>True when the machine can take this much more waste. A machine without a waste tank always can.</summary>
    public bool CanTake(EntityUid machine, float amount)
    {
        if (!TryComp<NCPharmaWasteTankComponent>(machine, out var tank))
            return true;

        return TryGetCanister(machine, tank, out _, out var contents) &&
               (float) contents.AvailableVolume >= amount * tank.UnitsPerWaste;
    }

    public void Add(EntityUid machine, float amount)
    {
        if (!TryComp<NCPharmaWasteTankComponent>(machine, out var tank) ||
            !TryGetCanister(machine, tank, out var solution, out _))
        {
            return;
        }

        _solutions.TryAddReagent(
            solution.Value, tank.Reagent.Id, FixedPoint2.New(MathF.Max(0f, amount) * tank.UnitsPerWaste));
    }

    private bool TryGetCanister(
        EntityUid machine,
        NCPharmaWasteTankComponent tank,
        [NotNullWhen(true)] out Entity<SolutionComponent>? solution,
        [NotNullWhen(true)] out Solution? contents)
    {
        solution = null;
        contents = null;
        return _slots.TryGetSlot(machine, tank.Slot, out var slot) &&
               slot.Item is { } canister &&
               HasComp<NCPharmaWasteCanisterComponent>(canister) &&
               _solutions.TryGetDrainableSolution(canister, out solution, out contents);
    }

    private void OnBinUsing(EntityUid uid, NCPharmaWasteBinComponent bin, InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCPharmaWasteCanisterComponent>(args.Used, out var canister) ||
            !_solutions.TryGetDrainableSolution(args.Used, out var solution, out var contents))
        {
            return;
        }

        args.Handled = true;
        if (!_power.IsPowered(uid))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), uid, args.User);
            return;
        }

        var waste = contents.GetTotalPrototypeQuantity(canister.Reagent);
        if (waste <= FixedPoint2.Zero)
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-nothing"), uid, args.User);
            return;
        }

        _solutions.RemoveReagent(solution.Value, canister.Reagent.Id, waste);
        canister.Age = TimeSpan.Zero;
        _popup.PopupEntity(Loc.GetString("nc-pharma-waste-disposed"), uid, args.User);
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
            // Carried, in a hand or sitting in a machine, a canister is looked after.
            if (_containers.IsEntityInContainer(uid) ||
                !_solutions.TryGetDrainableSolution(uid, out var solution, out var contents) ||
                contents.GetTotalPrototypeQuantity(canister.Reagent) <= FixedPoint2.Zero)
            {
                canister.Age = TimeSpan.Zero;
                continue;
            }

            canister.Age += CheckInterval;
            if (canister.Age < canister.LeakAfter)
                continue;

            canister.Age = TimeSpan.Zero;
            _solutions.RemoveReagent(solution.Value, canister.Reagent.Id, FixedPoint2.New(canister.LeakUnits));
            var fumes = new Solution();
            fumes.AddReagent(Fumes, FixedPoint2.New(8));
            var smoke = Spawn(SmokeEntity, Transform(uid).Coordinates);
            _smoke.StartSmoke(smoke, fumes, 10f, 4);
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-leak"), uid, PopupType.MediumCaution);
        }
    }
}
