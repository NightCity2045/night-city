// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Lab;

/// <summary>
/// The laboratory stations of the production chain: extraction of raw material, the sterile cabinet and the
/// formulation station, and the filling station. Each has one job, needs power, takes time, makes waste, and
/// asks the action-resolution layer how well the procedure went.
/// </summary>
public sealed partial class NCPharmaStationSystem : EntitySystem
{
    private const float InteractionRange = 1.5f;

    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private NCBiotechActionSystem _actions = default!;
    [Dependency] private NCPharmaWasteSystem _waste = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaRawMaterialComponent, ExaminedEvent>(OnRawExamined);
        SubscribeLocalEvent<NCPharmaSourceComponent, ExaminedEvent>(OnSourceExamined);
        SubscribeLocalEvent<NCPharmaExtractorComponent, ActivateInWorldEvent>(OnExtractorActivate);
        SubscribeLocalEvent<NCPharmaExtractorComponent, NCPharmaExtractDoAfterEvent>(OnExtractDone);
        SubscribeLocalEvent<NCPharmaTreatmentStationComponent, ActivateInWorldEvent>(OnTreatActivate);
        SubscribeLocalEvent<NCPharmaTreatmentStationComponent, NCPharmaTreatDoAfterEvent>(OnTreatDone);
        SubscribeLocalEvent<NCPharmaFillingStationComponent, ActivateInWorldEvent>(OnFillActivate);
        SubscribeLocalEvent<NCPharmaFillingStationComponent, NCPharmaFillDoAfterEvent>(OnFillDone);
    }

    private void OnRawExamined(Entity<NCPharmaRawMaterialComponent> raw, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("nc-pharma-raw-examine", ("origin", Loc.GetString(raw.Comp.Origin))));
        if (raw.Comp.Feedstock is { } feedstock && _prototypes.TryIndex(feedstock, out var type))
            args.PushMarkup(Loc.GetString("nc-pharma-feedstock-examine", ("type", Loc.GetString(type.Name))));
    }

    private void OnSourceExamined(Entity<NCPharmaSourceComponent> source, ref ExaminedEvent args)
    {
        if (source.Comp.Feedstock is { } feedstock && _prototypes.TryIndex(feedstock, out var type))
            args.PushMarkup(Loc.GetString("nc-pharma-feedstock-examine", ("type", Loc.GetString(type.Name))));
    }

    private EntityUid? GetLoaded(EntityUid machine, string slotId)
    {
        return _slots.TryGetSlot(machine, slotId, out var slot) ? slot.Item : null;
    }

    private bool TryStart(
        EntityUid machine,
        EntityUid user,
        string slotId,
        TimeSpan duration,
        float waste,
        DoAfterEvent doAfterEvent)
    {
        if (!_power.IsPowered(machine))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-unpowered"), machine, user);
            return false;
        }

        if (GetLoaded(machine, slotId) == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-empty"), machine, user);
            return false;
        }

        if (!_waste.CanTake(machine, waste))
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-waste-full"), machine, user);
            return false;
        }

        var args = new DoAfterArgs(EntityManager, user, duration, doAfterEvent, machine, target: machine)
        {
            BreakOnMove = true,
            DistanceThreshold = InteractionRange,
        };
        if (!_doAfter.TryStartDoAfter(args))
            return false;

        _popup.PopupEntity(Loc.GetString("nc-lab-machine-started"), machine, user);
        return true;
    }

    // ---- extraction -----------------------------------------------------------------------------------

    private void OnExtractorActivate(Entity<NCPharmaExtractorComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        TryStart(machine, args.User, machine.Comp.Slot, machine.Comp.Duration, machine.Comp.WastePerRun,
            new NCPharmaExtractDoAfterEvent());
    }

    private void OnExtractDone(Entity<NCPharmaExtractorComponent> machine, ref NCPharmaExtractDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(machine.Owner) ||
            GetLoaded(machine, machine.Comp.Slot) is not { } item ||
            !_waste.CanTake(machine, machine.Comp.WastePerRun))
        {
            return;
        }

        // Ready-made raw material, or a container with a brew the players cooked up themselves.
        float units, purity, contaminants;
        EntProtoId output;
        string? unknownPool = null;
        ProtoId<NCPharmaFeedstockPrototype>? feedstock = null;
        Entity<SolutionComponent>? brew = null;
        if (TryComp<NCPharmaRawMaterialComponent>(item, out var raw))
        {
            (units, purity, contaminants, output, unknownPool, feedstock) =
                (raw.Units, raw.Purity, raw.Contaminants, raw.Output, raw.UnknownPool, raw.Feedstock);
        }
        else if (TryReadBrew(machine.Comp, item, out var read))
        {
            (units, purity, contaminants, output, feedstock) =
                (read.Units, read.Purity, read.Contaminants, machine.Comp.BrewOutput, read.Feedstock);
            brew = read.Solution;
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("nc-pharma-brew-unusable"), machine, args.User);
            return;
        }

        _waste.Add(machine, machine.Comp.WastePerRun);
        var coordinates = Transform(machine).Coordinates;
        if (unknownPool is { } pool)
        {
            _actions.Resolve(args.User, "NCBiotechIsolate", machine.Owner);
            if (_batches.TryIsolateCompound(pool, item, Name(args.User), out var formula, out var codename))
            {
                QueueDel(item);
                _popup.PopupEntity(Loc.GetString("nc-pharma-isolated", ("name", codename)), machine, args.User);
                return;
            }

            _popup.PopupEntity(Loc.GetString("nc-pharma-isolation-failed"), machine, args.User);
            return;
        }

        var quality = _actions.Resolve(args.User, "NCBiotechExtract", machine.Owner).Quality;
        var extract = Spawn(output, coordinates);
        if (TryComp<NCPharmaSourceComponent>(extract, out var source))
        {
            source.Units = units * machine.Comp.Efficiency * quality;
            source.Purity = Math.Clamp(purity * machine.Comp.PurityRetention, 0f, 1f);
            source.Feedstock = feedstock;
            Dirty(extract, source);
        }

        // The extract inherits the hidden contaminants of the raw material.
        EnsureComp<NCPharmaSourceHiddenComponent>(extract).Contaminants = contaminants;

        // A brew is drawn out of the container, which stays in the machine; ready-made material is used up.
        if (brew is { } solution)
            _solutions.RemoveAllSolution(solution);
        else
            QueueDel(item);
        _hands.TryPickupAnyHand(args.User, extract);
        _popup.PopupEntity(Loc.GetString("nc-pharma-extracted"), machine, args.User);
    }

    /// <summary>
    /// Reads a player-brewed solution. The kind of raw material is the broth there is most of. The share of that
    /// broth in the whole solution is its purity and the share of sludge its hidden contamination, so a clean
    /// reaction gives a clean extract, a ruined one does not, and a mixture of kinds dilutes the result.
    /// </summary>
    private bool TryReadBrew(
        NCPharmaExtractorComponent extractor,
        EntityUid container,
        out (float Units, float Purity, float Contaminants, ProtoId<NCPharmaFeedstockPrototype> Feedstock,
            Entity<SolutionComponent> Solution) brew)
    {
        brew = default;
        if (!_solutions.TryGetDrainableSolution(container, out var soln, out var solution))
            return false;

        var total = (float) solution.Volume;
        if (total <= 0f)
            return false;

        NCPharmaFeedstockPrototype? best = null;
        var broth = 0f;
        foreach (var candidate in _prototypes.EnumeratePrototypes<NCPharmaFeedstockPrototype>())
        {
            var amount = (float) solution.GetTotalPrototypeQuantity(candidate.Broth);
            if (amount <= broth)
                continue;
            best = candidate;
            broth = amount;
        }

        if (best == null || broth < extractor.MinimumBrew)
            return false;

        var sludge = (float) solution.GetTotalPrototypeQuantity(extractor.SludgeReagent);
        var purity = Math.Clamp(broth / total, 0f, 1f) * extractor.BrewPurityCeiling;
        var contaminants = Math.Clamp(
            extractor.BrewBaseContaminants + sludge / total * extractor.SludgeContamination, 0f, 1f);
        brew = (broth * extractor.UnitsPerReagent, purity, contaminants, best.ID, soln.Value);
        return true;
    }

    // ---- sterile cabinet / formulation station ---------------------------------------------------------

    private void OnTreatActivate(Entity<NCPharmaTreatmentStationComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        TryStart(machine, args.User, machine.Comp.Slot, machine.Comp.Duration, machine.Comp.WastePerRun,
            new NCPharmaTreatDoAfterEvent());
    }

    private void OnTreatDone(Entity<NCPharmaTreatmentStationComponent> machine, ref NCPharmaTreatDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(machine.Owner) ||
            GetLoaded(machine, machine.Comp.Slot) is not { } item ||
            !TryComp<NCPharmaContainerComponent>(item, out var pack) ||
            !_waste.CanTake(machine, machine.Comp.WastePerRun))
        {
            return;
        }

        var procedure = machine.Comp.Treatment == NCPharmaTreatment.Sterilize ? "NCBiotechSterilize" : "NCBiotechFormulate";
        var quality = _actions.Resolve(args.User, procedure, machine.Owner, [pack.BatchId]).Quality;
        if (!_batches.TryTreatBatch(pack.BatchId, machine.Comp.Treatment, machine.Comp.GapClosed,
                machine.Comp.StabilityCost, quality, Name(args.User)))
        {
            _popup.PopupEntity(Loc.GetString("nc-lab-machine-wrong-sample"), machine, args.User);
            return;
        }

        _waste.Add(machine, machine.Comp.WastePerRun);
        _popup.PopupEntity(Loc.GetString("nc-pharma-treated"), machine, args.User);
    }

    // ---- filling station -------------------------------------------------------------------------------

    private void OnFillActivate(Entity<NCPharmaFillingStationComponent> machine, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        TryStart(machine, args.User, machine.Comp.Slot, machine.Comp.Duration, 0f, new NCPharmaFillDoAfterEvent());
    }

    private void OnFillDone(Entity<NCPharmaFillingStationComponent> machine, ref NCPharmaFillDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        if (!_power.IsPowered(machine.Owner) || GetLoaded(machine, machine.Comp.Slot) is not { } item)
            return;

        _actions.Resolve(args.User, "NCBiotechFill", machine.Owner);
        var packs = _batches.TryRepack(item, machine.Comp.DosesPerPack, Transform(machine).Coordinates, Name(args.User));
        _popup.PopupEntity(Loc.GetString(packs > 0 ? "nc-pharma-filled" : "nc-lab-machine-wrong-sample",
            ("count", packs)), machine, args.User);
    }
}
