// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Popups;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Production mode: a known formula is made from a physical raw-material canister. The run takes time, uses up
/// material and produces a hidden batch whose quality comes from the material, the equipment and the mode.
/// </summary>
public sealed partial class NCPharmaSynthesisSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private NCPharmaSourceSystem _sources = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private Content.Shared.Power.EntitySystems.SharedPowerReceiverSystem _power = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaSynthesizerComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaSynthesizerComponent, NCPharmaStationStartMessage>(OnStartMessage);
        SubscribeLocalEvent<NCPharmaSynthesizerComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<NCPharmaSynthesizerComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NCPharmaSynthesisRunComponent, NCPharmaSynthesizerComponent>();
        var lost = TimeSpan.FromSeconds(frameTime);
        while (query.MoveNext(out var uid, out var run, out var station))
        {
            // Without power the run stands still and resumes where it stopped.
            if (!_power.IsPowered(uid))
            {
                run.StartedAt += lost;
                run.FinishAt += lost;
                continue;
            }

            if (_timing.CurTime < run.FinishAt)
                continue;

            CompleteRun((uid, station), run);
        }
    }

    private void OnUiOpened(Entity<NCPharmaSynthesizerComponent> station, ref BoundUIOpenedEvent args)
    {
        UpdateUi(station, NCPharmaStationNotice.None);
    }

    private void OnSlotChanged(Entity<NCPharmaSynthesizerComponent> station, ref EntInsertedIntoContainerMessage args)
    {
        UpdateUi(station, NCPharmaStationNotice.None);
    }

    private void OnSlotChanged(Entity<NCPharmaSynthesizerComponent> station, ref EntRemovedFromContainerMessage args)
    {
        UpdateUi(station, NCPharmaStationNotice.None);
    }

    private void OnStartMessage(Entity<NCPharmaSynthesizerComponent> station, ref NCPharmaStationStartMessage args)
    {
        var notice = TryStartRun(station, args.Actor, args.Formula, args.Profile, args.Units);
        UpdateUi(station, notice);
    }

    /// <summary>
    /// Validates a production request and starts the run. Material is taken at the start: a run that is
    /// already under way cannot be undone by pulling the canister out.
    /// </summary>
    public NCPharmaStationNotice TryStartRun(
        Entity<NCPharmaSynthesizerComponent> station,
        EntityUid user,
        string formulaId,
        string profileId,
        int units)
    {
        if (HasComp<NCPharmaSynthesisRunComponent>(station))
            return NCPharmaStationNotice.Busy;

        if (!_sources.TryReadSlot(station, station.Comp.SourceSlot, out _) &&
            !_sources.TryReadSlot(station, station.Comp.SecondSourceSlot, out _))
        {
            return NCPharmaStationNotice.NoSource;
        }

        if (units < 1 || units > station.Comp.MaximumUnitsPerRun ||
            !_batches.TryGetFormula(formulaId, out var formula) ||
            !_prototypes.TryIndex<NCPharmaProductionProfilePrototype>(profileId, out var profile) ||
            formula.RegulatoryStatus is NCPharmaRegulatoryStatus.Banned or NCPharmaRegulatoryStatus.Recalled)
        {
            return NCPharmaStationNotice.InvalidRequest;
        }

        if (formula.RegulatoryStatus == NCPharmaRegulatoryStatus.Experimental &&
            _batches.TryGetTuning(out var tuning) &&
            units > tuning.MaximumExperimentalUnits)
        {
            return NCPharmaStationNotice.ExperimentalLimit;
        }

        // NC: each compound can only be made from the raw material it accepts; an alternative gives fewer doses.
        // A formula of two compounds may take each of them from its own canister.
        if (!_batches.TryGetCoreProfiles(formula, out var cores))
            return NCPharmaStationNotice.InvalidRequest;
        switch (_sources.TryPlanDraw(cores, station, station.Comp.SourceSlot, station.Comp.SecondSourceSlot, units, out var draw))
        {
            case NCPharmaDrawResult.NoSource:
                return NCPharmaStationNotice.NoSource;
            case NCPharmaDrawResult.NotEnoughSource:
                return NCPharmaStationNotice.NotEnoughSource;
            case NCPharmaDrawResult.WrongFeedstock:
                return NCPharmaStationNotice.WrongFeedstock;
        }

        // NC: a modifier may ask for a reagent from the vial in the additive slot; the run uses it up when it starts.
        // Every modifier that asks gets its share; two asking for the same reagent need both amounts.
        var required = new Dictionary<ProtoId<ReagentPrototype>, float>();
        foreach (var modifierId in formula.Modifiers)
        {
            if (_prototypes.TryIndex(modifierId, out var found) && found.RequiredReagent is { } reagent)
                required[reagent] = required.GetValueOrDefault(reagent) + found.RequiredAmount;
        }

        Entity<SolutionComponent>? additiveSolution = null;
        if (required.Count > 0)
        {
            if (GetAdditive(station) is not { } vial ||
                !_solutions.TryGetDrainableSolution(vial, out additiveSolution, out var vialContents) ||
                required.Any(pair => (float) vialContents.GetTotalPrototypeQuantity(pair.Key) < pair.Value))
            {
                return NCPharmaStationNotice.MissingAdditive;
            }
        }

        var inputs = new NCPharmaProductionInputs(
            units * draw.Plan.Yield,
            draw.Purity,
            station.Comp.EquipmentSterility,
            station.Comp.EquipmentMixing,
            station.Comp.CalibrationError,
            draw.Contaminants); // NC

        // NC: the material is liquid in the canister; the run takes its share and leaves the rest as it was.
        _sources.Consume(draw, units);
        if (additiveSolution is { } drawn)
        {
            foreach (var (used, amount) in required)
                _solutions.RemoveReagent(drawn, used.Id, FixedPoint2.New(amount));
        }

        var run = EnsureComp<NCPharmaSynthesisRunComponent>(station);
        run.Formula = formula.ID;
        run.Profile = profile.ID;
        run.Inputs = inputs;
        run.Producer = ToPrettyString(user);
        run.StartedAt = _timing.CurTime;
        run.FinishAt = _timing.CurTime + profile.Duration;
        return NCPharmaStationNotice.Started;
    }

    private void CompleteRun(Entity<NCPharmaSynthesizerComponent> station, NCPharmaSynthesisRunComponent run)
    {
        var formula = run.Formula;
        var profile = run.Profile;
        var inputs = run.Inputs;
        var producer = run.Producer;
        RemComp<NCPharmaSynthesisRunComponent>(station);

        if (!_batches.TryCreateBatch(formula, profile, inputs, producer, ToPrettyString(station), out var batch))
        {
            Log.Error($"Synthesis of {formula} at {ToPrettyString(station)} produced no batch.");
            _popup.PopupEntity(Loc.GetString("nc-pharma-station-failed"), station);
            UpdateUi(station, NCPharmaStationNotice.Failed);
            return;
        }

        var coordinates = Transform(station).Coordinates;
        var remaining = batch.DosesProduced;
        var packs = 0;
        while (remaining > 0 && packs < station.Comp.MaximumPacksPerRun)
        {
            var doses = Math.Min(station.Comp.DosesPerPack, remaining);
            if (_batches.TrySpawnContainer(_batches.ResolvePack(batch.Formula, station.Comp.OutputContainer), coordinates, batch.BatchId, doses, producer) == null)
                break;
            remaining -= doses;
            packs++;
        }

        _popup.PopupEntity(Loc.GetString("nc-pharma-station-finished", ("code", batch.BatchCode)), station);
        UpdateUi(station, NCPharmaStationNotice.Finished);
    }

    private NCPharmaReagentAmount[] AdditiveContents(Entity<NCPharmaSynthesizerComponent> station)
    {
        if (GetAdditive(station) is not { } vial || !_solutions.TryGetDrainableSolution(vial, out _, out var contents))
            return [];

        return contents.Contents
            .Select(reagent => new NCPharmaReagentAmount(reagent.Reagent.Prototype, (float) reagent.Quantity))
            .ToArray();
    }

    private EntityUid? GetAdditive(Entity<NCPharmaSynthesizerComponent> station)
    {
        return _itemSlots.TryGetSlot(station, station.Comp.AdditiveSlot, out var slot) ? slot.Item : null;
    }

    private void UpdateUi(Entity<NCPharmaSynthesizerComponent> station, NCPharmaStationNotice notice)
    {
        if (!_ui.IsUiOpen(station.Owner, NCPharmaStationUiKey.Key))
            return;

        var hasSource = _sources.TryReadSlot(station, station.Comp.SourceSlot, out var source);
        var hasSecond = _sources.TryReadSlot(station, station.Comp.SecondSourceSlot, out var second);
        var busy = TryComp<NCPharmaSynthesisRunComponent>(station, out var run);
        var progress = 0f;
        if (busy && run != null && run.FinishAt > run.StartedAt)
        {
            progress = (float) Math.Clamp(
                (_timing.CurTime - run.StartedAt).TotalSeconds / (run.FinishAt - run.StartedAt).TotalSeconds, 0d, 1d);
        }

        _ui.SetUiState(
            station.Owner,
            NCPharmaStationUiKey.Key,
            new NCPharmaStationState(
                hasSource,
                hasSource ? source.Units : 0f,
                hasSource ? source.Purity : 0f,
                busy,
                progress,
                notice,
                _batches.BuildFormulaSummaries(true),
                hasSource ? source.Feedstock : null,
                AdditiveContents(station),
                hasSecond ? new NCPharmaSourceSummary(second.Units, second.Purity, second.Feedstock) : null));
    }
}
