// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Spreads a dose over its absorption time. It has no update loop of its own: it advances through the central
/// medical scheduler, only while a body actually has something left to absorb.
/// </summary>
public sealed partial class NCPharmaDepotSystem : EntitySystem
{
    private const int MaximumEntriesPerBody = 16;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCPhysiologySchedulerSystem _scheduler = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaDepotComponent, NCMedicalScheduledUpdateEvent>(OnScheduledUpdate);
        SubscribeLocalEvent<NCPharmaDepotComponent, NCMedicalNextUpdateEvent>(OnNextUpdate);
        SubscribeLocalEvent<NCPharmaDepotComponent, NCPharmacologyDepotCaptureEvent>(OnCapture);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCPharmacologyDepotRestoreEvent>(OnRestore);
    }

    /// <summary>
    /// Delivers a dose to the body over the given time. A zero duration, a full depot or a failed deposit
    /// delivers the dose at once, so a dose is never silently lost.
    /// </summary>
    public bool TryDeposit(
        EntityUid body,
        ProtoId<NCDrugProfilePrototype> drug,
        float concentration,
        TimeSpan duration,
        float clearanceFactor = 1f)
    {
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !float.IsFinite(concentration) || concentration <= 0f)
        {
            return false;
        }

        var seconds = (float) duration.TotalSeconds;
        if (seconds > 0f && !_pharmacology.TryEnsureState(body))
            return false;

        var depot = EnsureComp<NCPharmaDepotComponent>(body);
        if (seconds <= 0f || depot.Entries.Count >= MaximumEntriesPerBody)
        {
            TidyDepot(body, depot);
            return _pharmacology.ApplyDose(body, drug, concentration, clearanceFactor).Accepted;
        }

        depot.Entries.Add(new NCPharmaDepotEntry
        {
            Drug = drug,
            Remaining = concentration,
            RatePerSecond = concentration / seconds,
            ClearanceFactor = clearanceFactor,
        });
        _scheduler.Schedule(body, UpdateInterval);
        return true;
    }

    private void OnScheduledUpdate(Entity<NCPharmaDepotComponent> body, ref NCMedicalScheduledUpdateEvent args)
    {
        var seconds = (float) args.Elapsed.TotalSeconds;
        if (seconds <= 0f)
            return;

        for (var i = body.Comp.Entries.Count - 1; i >= 0; i--)
        {
            var entry = body.Comp.Entries[i];
            var delivered = MathF.Min(entry.Remaining, entry.RatePerSecond * seconds);
            if (delivered > 0f)
                _pharmacology.ApplyDose(body.Owner, entry.Drug, delivered, entry.ClearanceFactor);

            entry.Remaining -= delivered;
            if (entry.Remaining <= 0.0001f)
                body.Comp.Entries.RemoveAt(i);
        }

        TidyDepot(body.Owner, body.Comp);
    }

    private void OnCapture(Entity<NCPharmaDepotComponent> body, ref NCPharmacologyDepotCaptureEvent args)
    {
        foreach (var entry in body.Comp.Entries)
        {
            args.Doses.Add(new NCPendingDoseSnapshot
            {
                Drug = entry.Drug,
                Remaining = entry.Remaining,
                RatePerSecond = entry.RatePerSecond,
                ClearanceFactor = entry.ClearanceFactor,
            });
        }
    }

    private void OnRestore(Entity<NCMedicalBodyComponent> body, ref NCPharmacologyDepotRestoreEvent args)
    {
        RemComp<NCPharmaDepotComponent>(body);
        if (args.Doses.Count == 0)
            return;

        var depot = EnsureComp<NCPharmaDepotComponent>(body);
        foreach (var dose in args.Doses.Take(MaximumEntriesPerBody))
        {
            depot.Entries.Add(new NCPharmaDepotEntry
            {
                Drug = dose.Drug,
                Remaining = dose.Remaining,
                RatePerSecond = dose.RatePerSecond,
                ClearanceFactor = dose.ClearanceFactor,
            });
        }

        _scheduler.Schedule(body, UpdateInterval);
    }

    private void OnNextUpdate(Entity<NCPharmaDepotComponent> body, ref NCMedicalNextUpdateEvent args)
    {
        if (body.Comp.Entries.Count == 0)
            return;

        if (args.Delay == null || UpdateInterval < args.Delay)
            args.Delay = UpdateInterval;
    }

    private void TidyDepot(EntityUid body, NCPharmaDepotComponent depot)
    {
        if (depot.Entries.Count == 0)
            RemCompDeferred<NCPharmaDepotComponent>(body);
    }
}
