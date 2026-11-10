// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Laboratory analysis. The analyzer measures only the tests the operator asked for and only then does the
/// batch record learn anything: knowledge of the real composition is paid for in time and in doses.
/// </summary>
public sealed partial class NCPharmaQcSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaQcAnalyzerComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaQcAnalyzerComponent, NCPharmaQcRunMessage>(OnRunMessage);
        SubscribeLocalEvent<NCPharmaQcAnalyzerComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<NCPharmaQcAnalyzerComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NCPharmaQcRunComponent, NCPharmaQcAnalyzerComponent>();
        while (query.MoveNext(out var uid, out var run, out var analyzer))
        {
            if (_timing.CurTime < run.FinishAt)
                continue;

            CompleteRun((uid, analyzer), run);
        }
    }

    private void OnUiOpened(Entity<NCPharmaQcAnalyzerComponent> analyzer, ref BoundUIOpenedEvent args)
    {
        UpdateUi(analyzer, NCPharmaQcNotice.None);
    }

    private void OnSlotChanged(Entity<NCPharmaQcAnalyzerComponent> analyzer, ref EntInsertedIntoContainerMessage args)
    {
        UpdateUi(analyzer, NCPharmaQcNotice.None);
    }

    private void OnSlotChanged(Entity<NCPharmaQcAnalyzerComponent> analyzer, ref EntRemovedFromContainerMessage args)
    {
        UpdateUi(analyzer, NCPharmaQcNotice.None);
    }

    private void OnRunMessage(Entity<NCPharmaQcAnalyzerComponent> analyzer, ref NCPharmaQcRunMessage args)
    {
        UpdateUi(analyzer, TryStartRun(analyzer, args.Actor, args.Tests));
    }

    /// <summary>
    /// Takes one dose of the inserted pack as the sample and starts measuring the requested tests.
    /// </summary>
    public NCPharmaQcNotice TryStartRun(Entity<NCPharmaQcAnalyzerComponent> analyzer, EntityUid user, NCPharmaQcTests tests)
    {
        tests &= NCPharmaQcTests.All;
        if (tests == NCPharmaQcTests.None)
            return NCPharmaQcNotice.NoTestsSelected;
        if (HasComp<NCPharmaQcRunComponent>(analyzer))
            return NCPharmaQcNotice.Busy;
        if (!TryGetSample(analyzer, out var packUid, out var pack))
            return NCPharmaQcNotice.NoSample;
        if (pack.Doses < 1)
            return NCPharmaQcNotice.NoDoseLeft;
        if (!_batches.TryGetBatch(pack.BatchId, out _))
            return NCPharmaQcNotice.UnknownBatch;

        pack.Doses--;
        Dirty(packUid, pack);

        var run = EnsureComp<NCPharmaQcRunComponent>(analyzer);
        run.BatchId = pack.BatchId;
        run.Tests = tests;
        run.Operator = ToPrettyString(user);
        run.StartedAt = _timing.CurTime;
        run.FinishAt = _timing.CurTime + analyzer.Comp.TimePerTest * CountTests(tests);
        return NCPharmaQcNotice.Started;
    }

    private void CompleteRun(Entity<NCPharmaQcAnalyzerComponent> analyzer, NCPharmaQcRunComponent run)
    {
        var batchId = run.BatchId;
        var tests = run.Tests;
        var operatorRef = run.Operator;
        RemComp<NCPharmaQcRunComponent>(analyzer);

        if (!_batches.TryPerformQc(batchId, operatorRef, tests, out var report) ||
            !_batches.TryGetBatch(batchId, out var batch))
        {
            UpdateUi(analyzer, NCPharmaQcNotice.UnknownBatch);
            return;
        }

        analyzer.Comp.LastReport = report;
        analyzer.Comp.LastLabel = batch.Label;
        analyzer.Comp.LastBatchState = batch.QcState;
        _popup.PopupEntity(Loc.GetString("nc-pharma-qc-finished", ("code", batch.BatchCode)), analyzer);
        UpdateUi(analyzer, NCPharmaQcNotice.Finished);
    }

    private bool TryGetSample(
        Entity<NCPharmaQcAnalyzerComponent> analyzer,
        out EntityUid packUid,
        out NCPharmaContainerComponent pack)
    {
        packUid = default;
        pack = default!;
        if (!_itemSlots.TryGetSlot(analyzer, analyzer.Comp.SampleSlot, out var slot) ||
            slot.Item is not { } item ||
            !TryComp(item, out NCPharmaContainerComponent? found))
        {
            return false;
        }

        packUid = item;
        pack = found;
        return true;
    }

    private void UpdateUi(Entity<NCPharmaQcAnalyzerComponent> analyzer, NCPharmaQcNotice notice)
    {
        var hasSample = TryGetSample(analyzer, out _, out var pack);
        NCPharmaLabel? label = null;
        var batchState = NCPharmaQcState.Untested;
        if (hasSample && _batches.TryGetBatch(pack.BatchId, out var batch))
        {
            label = batch.Label;
            batchState = batch.QcState;
        }

        var busy = TryComp<NCPharmaQcRunComponent>(analyzer, out var run);
        var progress = 0f;
        if (busy && run != null && run.FinishAt > run.StartedAt)
        {
            progress = (float) Math.Clamp(
                (_timing.CurTime - run.StartedAt).TotalSeconds / (run.FinishAt - run.StartedAt).TotalSeconds, 0d, 1d);
        }

        // The last report stays on screen only while it belongs to the pack that is still inserted.
        var report = analyzer.Comp.LastReport is { } last && hasSample && last.BatchId == pack.BatchId ? last : (NCPharmaQcReport?) null;
        _ui.SetUiState(
            analyzer.Owner,
            NCPharmaQcUiKey.Key,
            new NCPharmaQcMachineState(
                hasSample,
                hasSample ? pack.Doses : 0,
                label,
                report,
                batchState,
                busy,
                progress,
                notice));
    }

    private static int CountTests(NCPharmaQcTests tests) => System.Numerics.BitOperations.PopCount((uint) tests);
}
