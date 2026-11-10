// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Chemistry.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using Content.Shared.Popups;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Fluids.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Manual synthesis. The operator holds a reaction inside its window while it releases heat, builds pressure
/// and drifts; every accident has a cause the gauges showed and an action that would have prevented it.
/// Only formulas under development are made this way: a known formula is produced by the automated line.
/// </summary>
public sealed partial class NCPharmaReactorSystem : EntitySystem
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
    private static readonly ProtoId<NCPharmaProductionProfilePrototype> ManualProfile = "NCPharmaProductionManual";
    private static readonly EntProtoId SmokeEntity = "Smoke";
    private static readonly ProtoId<Content.Shared.Chemistry.Reagent.ReagentPrototype> Fumes = "NCReactorFumes";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SmokeSystem _smoke = default!;
    [Dependency] private ExplosionSystem _explosion = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaReactorComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaReactorComponent, NCPharmaReactorStartMessage>(OnStart);
        SubscribeLocalEvent<NCPharmaReactorComponent, NCPharmaReactorControlMessage>(OnControl);
        SubscribeLocalEvent<NCPharmaReactorComponent, NCPharmaReactorActionMessage>(OnAction);
        SubscribeLocalEvent<NCPharmaReactorComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<NCPharmaReactorComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NCPharmaReactorRunComponent, NCPharmaReactorComponent>();
        while (query.MoveNext(out var uid, out var run, out var reactor))
        {
            // A long frame is caught up, but never by more than a few steps, so a hitch cannot skip an accident.
            var steps = 0;
            while (_timing.CurTime >= run.NextStep && steps++ < 4)
            {
                run.NextStep += Step;
                if (!StepRun((uid, reactor), run))
                    break;
            }
        }
    }

    private void OnUiOpened(Entity<NCPharmaReactorComponent> reactor, ref BoundUIOpenedEvent args) =>
        UpdateUi(reactor, NCPharmaReactorNotice.None);

    private void OnSlotChanged(Entity<NCPharmaReactorComponent> reactor, ref EntInsertedIntoContainerMessage args) =>
        UpdateUi(reactor, NCPharmaReactorNotice.None);

    private void OnSlotChanged(Entity<NCPharmaReactorComponent> reactor, ref EntRemovedFromContainerMessage args) =>
        UpdateUi(reactor, NCPharmaReactorNotice.None);

    private void OnStart(Entity<NCPharmaReactorComponent> reactor, ref NCPharmaReactorStartMessage args) =>
        UpdateUi(reactor, TryStartRun(reactor, args.Actor, args.Formula, args.Units));

    private void OnControl(Entity<NCPharmaReactorComponent> reactor, ref NCPharmaReactorControlMessage args)
    {
        if (!TryComp<NCPharmaReactorRunComponent>(reactor, out var run))
            return;

        run.Controls = new NCReactorControls(
            Math.Clamp(args.Heater, 0, NCReactorMath.MaximumLevel),
            Math.Clamp(args.Cooler, 0, NCReactorMath.MaximumLevel),
            args.Stirring,
            args.Venting);
        run.Ventilation = args.Ventilation;
        UpdateUi(reactor, NCPharmaReactorNotice.None);
    }

    private void OnAction(Entity<NCPharmaReactorComponent> reactor, ref NCPharmaReactorActionMessage args)
    {
        if (!TryComp<NCPharmaReactorRunComponent>(reactor, out var run) ||
            !_prototypes.TryIndex(run.Reaction, out var profile) ||
            !_batches.TryGetTuning(out var tuning))
        {
            return;
        }

        switch (args.Action)
        {
            case NCPharmaReactorAction.AddCatalyst:
                if (run.CatalystLeft <= 0 || run.State.Catalyst >= 1f)
                {
                    UpdateUi(reactor, NCPharmaReactorNotice.NoCatalystLeft);
                    return;
                }

                run.CatalystLeft = Math.Min(run.CatalystLeft - 1, tuning.CatalystCharges);
                run.State.Catalyst = MathF.Min(1f, run.State.Catalyst + 0.5f);
                UpdateUi(reactor, NCPharmaReactorNotice.CatalystUsed);
                break;
            case NCPharmaReactorAction.Quench:
                // Quenching stops the reaction for good: the run ends at once with whatever it had reached.
                run.State.Quenched = true;
                run.State.Temperature = MathF.Max(profile.AmbientTemperature, run.State.Temperature - 40f);
                Finish(reactor, run, NCPharmaReactorNotice.Quenched);
                break;
            case NCPharmaReactorAction.EmergencyStop:
                RemComp<NCPharmaReactorRunComponent>(reactor);
                UpdateUi(reactor, NCPharmaReactorNotice.Stopped);
                break;
        }
    }

    /// <summary>
    /// Starts a manual run of an experimental formula from a raw-material canister. The material is taken at
    /// the start, as a reaction that is under way cannot be undone.
    /// </summary>
    public NCPharmaReactorNotice TryStartRun(
        Entity<NCPharmaReactorComponent> reactor,
        EntityUid user,
        string formulaId,
        int units)
    {
        if (HasComp<NCPharmaReactorRunComponent>(reactor))
            return NCPharmaReactorNotice.Busy;
        if (!TryGetSource(reactor, out var sourceUid, out var source))
            return NCPharmaReactorNotice.NoSource;
        if (!_batches.TryGetTuning(out var tuning) ||
            !_batches.TryGetFormula(formulaId, out var formula))
        {
            return NCPharmaReactorNotice.InvalidRequest;
        }

        if (formula.RegulatoryStatus != NCPharmaRegulatoryStatus.Experimental)
            return NCPharmaReactorNotice.NotExperimental;
        if (units < 1 || units > tuning.MaximumReactorUnits)
            return NCPharmaReactorNotice.UnitsLimit;
        if (source.Units < units)
            return NCPharmaReactorNotice.NotEnoughSource;

        var reactionId = formula.Reaction ?? tuning.DefaultReaction;
        if (!_prototypes.TryIndex(reactionId, out var profile))
            return NCPharmaReactorNotice.InvalidRequest;

        source.Units -= units;
        Dirty(sourceUid, source);
        if (source.Units <= 0f)
            QueueDel(sourceUid);

        var run = EnsureComp<NCPharmaReactorRunComponent>(reactor);
        run.Formula = formula.ID;
        run.Reaction = reactionId;
        run.State = NCReactorMath.Start(profile);
        run.Controls = default;
        run.Ventilation = false;
        run.CatalystLeft = tuning.CatalystCharges;
        run.SourcePurity = source.Purity;
        run.Units = units;
        run.Operator = ToPrettyString(user);
        run.NextStep = _timing.CurTime + Step;
        run.Warned = false;
        run.Released = false;
        return NCPharmaReactorNotice.Started;
    }

    /// <summary>Advances one second. Returns false when the run ended and nothing more should be stepped.</summary>
    private bool StepRun(Entity<NCPharmaReactorComponent> reactor, NCPharmaReactorRunComponent run)
    {
        if (!_prototypes.TryIndex(run.Reaction, out var profile))
        {
            RemComp<NCPharmaReactorRunComponent>(reactor);
            return false;
        }

        NCReactorMath.Step(profile, run.State, run.Controls, 1f);
        if (CheckAccidents(reactor, run, profile))
            return false;

        if (run.State.Progress >= 1f)
        {
            Finish(reactor, run, NCPharmaReactorNotice.Finished);
            return false;
        }

        UpdateUi(reactor, NCPharmaReactorNotice.None);
        return true;
    }

    /// <summary>
    /// Applies the accident levels. Each level has its own threshold in the reaction profile; levels I and II
    /// happen once, and the rupture ends the run. Returns true when the vessel was destroyed.
    /// </summary>
    private bool CheckAccidents(
        Entity<NCPharmaReactorComponent> reactor,
        NCPharmaReactorRunComponent run,
        NCPharmaReactionProfilePrototype profile)
    {
        var state = run.State;
        if (!run.Warned && state.Instability >= profile.WarningInstability)
        {
            run.Warned = true;
            _popup.PopupEntity(Loc.GetString("nc-pharma-reactor-popup-warning"), reactor, PopupType.MediumCaution);
        }

        if (!state.Degraded && state.Instability >= profile.DegradeInstability)
        {
            state.Degraded = true;
            _popup.PopupEntity(Loc.GetString("nc-pharma-reactor-popup-degraded"), reactor, PopupType.MediumCaution);
        }

        if (!run.Released && state.Instability >= profile.ReleaseInstability)
        {
            run.Released = true;
            ReleaseFumes(reactor, run);
        }

        if (state.Instability < profile.RuptureInstability)
            return false;

        Rupture(reactor, run, profile);
        return true;
    }

    private void ReleaseFumes(Entity<NCPharmaReactorComponent> reactor, NCPharmaReactorRunComponent run)
    {
        var amount = reactor.Comp.FumesPerRelease * (run.Ventilation ? 0.4f : 1f);
        var solution = new Solution();
        solution.AddReagent(Fumes, FixedPoint2.New(amount));
        var smoke = Spawn(SmokeEntity, Transform(reactor).Coordinates);
        _smoke.StartSmoke(smoke, solution, 12f, run.Ventilation ? 3 : 7);
        _popup.PopupEntity(Loc.GetString("nc-pharma-reactor-popup-release"), reactor, PopupType.LargeCaution);
    }

    private void Rupture(
        Entity<NCPharmaReactorComponent> reactor,
        NCPharmaReactorRunComponent run,
        NCPharmaReactionProfilePrototype profile)
    {
        // Level IV needs a reaction that is hazardous by nature and a run pushed to the very edge.
        var severe = profile.Hazardous && run.State.Instability >= 0.98f;
        _popup.PopupEntity(
            Loc.GetString(severe ? "nc-pharma-reactor-popup-severe" : "nc-pharma-reactor-popup-rupture"),
            reactor,
            PopupType.LargeCaution);
        if (severe)
            _explosion.QueueExplosion(reactor, "Default", 60f, 4f, 30f, canCreateVacuum: false);
        else
            _explosion.QueueExplosion(reactor, "FireBomb", 8f, 4f, 6f, canCreateVacuum: false);

        RemComp<NCPharmaReactorRunComponent>(reactor);
        QueueDel(reactor);
    }

    /// <summary>
    /// Ends a run and turns what it reached into a batch. Purity, homogeneity and yield come from how the run
    /// was held, so a careless operator gets a poor batch from the same material.
    /// </summary>
    private void Finish(
        Entity<NCPharmaReactorComponent> reactor,
        NCPharmaReactorRunComponent run,
        NCPharmaReactorNotice notice)
    {
        var formula = run.Formula;
        var outcome = NCReactorMath.Evaluate(run.State);
        var units = (int) MathF.Floor(run.Units * outcome.YieldFactor);
        var sourcePurity = run.SourcePurity;
        var producer = run.Operator;
        var degraded = run.State.Degraded;
        RemComp<NCPharmaReactorRunComponent>(reactor);

        if (units < 1)
        {
            UpdateUi(reactor, NCPharmaReactorNotice.Degraded);
            return;
        }

        var inputs = new NCPharmaProductionInputs(
            units,
            sourcePurity * outcome.PurityFactor,
            reactor.Comp.EquipmentSterility,
            reactor.Comp.EquipmentMixing * outcome.HomogeneityFactor,
            0f);
        if (!_batches.TryCreateBatch(formula, ManualProfile, inputs, producer, ToPrettyString(reactor), out var batch))
        {
            UpdateUi(reactor, NCPharmaReactorNotice.Degraded);
            return;
        }

        var remaining = batch.DosesProduced;
        var packs = 0;
        var coordinates = Transform(reactor).Coordinates;
        while (remaining > 0 && packs < reactor.Comp.MaximumPacksPerRun)
        {
            var doses = Math.Min(reactor.Comp.DosesPerPack, remaining);
            if (_batches.TrySpawnContainer(reactor.Comp.OutputContainer, coordinates, batch.BatchId, doses, producer) == null)
                break;
            remaining -= doses;
            packs++;
        }

        _popup.PopupEntity(Loc.GetString("nc-pharma-reactor-popup-finished", ("code", batch.BatchCode)), reactor);
        UpdateUi(reactor, degraded ? NCPharmaReactorNotice.Degraded : notice);
    }

    private bool TryGetSource(
        Entity<NCPharmaReactorComponent> reactor,
        out EntityUid sourceUid,
        out NCPharmaSourceComponent source)
    {
        sourceUid = default;
        source = default!;
        if (!_itemSlots.TryGetSlot(reactor, reactor.Comp.SourceSlot, out var slot) ||
            slot.Item is not { } item ||
            !TryComp(item, out NCPharmaSourceComponent? found))
        {
            return false;
        }

        sourceUid = item;
        source = found;
        return true;
    }

    private void UpdateUi(Entity<NCPharmaReactorComponent> reactor, NCPharmaReactorNotice notice)
    {
        var hasSource = TryGetSource(reactor, out _, out var source);
        var formulas = _batches.BuildFormulaSummaries(false)
            .Where(formula => formula.Status == NCPharmaRegulatoryStatus.Experimental)
            .ToArray();
        TryComp<NCPharmaReactorRunComponent>(reactor, out var run);
        var state = run?.State ?? new NCReactorState();
        var warnings = NCReactorWarnings.None;
        if (run != null && _prototypes.TryIndex(run.Reaction, out var profile))
            warnings = NCReactorMath.ResolveWarnings(profile, state);

        _ui.SetUiState(
            reactor.Owner,
            NCPharmaReactorUiKey.Key,
            new NCPharmaReactorState(
                run != null,
                run?.Formula ?? string.Empty,
                run?.Reaction.Id ?? string.Empty,
                state.Temperature,
                state.TemperatureRate,
                state.Pressure,
                state.Mixing,
                state.Catalyst,
                state.Progress,
                state.Instability,
                run?.Controls.Heater ?? 0,
                run?.Controls.Cooler ?? 0,
                run?.Controls.Stirring ?? false,
                run?.Controls.Venting ?? false,
                run?.Ventilation ?? false,
                run?.CatalystLeft ?? 0,
                hasSource,
                hasSource ? source.Units : 0f,
                hasSource ? source.Purity : 0f,
                warnings,
                notice,
                formulas));
    }
}
