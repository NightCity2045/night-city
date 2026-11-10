// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>One entry of the append-only history of a batch. It is server-only and never replicated.</summary>
public sealed record NCPharmaAuditEntry(
    NCActiveWorldTime Time,
    NCPharmaAuditKind Kind,
    string Actor,
    string Detail);

/// <summary>
/// Hidden truth of one production run. <see cref="Actual"/> is never sent to a client: players see the label
/// until a laboratory run reveals measured values.
/// </summary>
public sealed class NCPharmaBatchRecord
{
    public Guid BatchId;
    public string BatchCode = string.Empty;
    public string Formula = string.Empty;
    public int FormulaVersion;
    public NCPharmaQuality Actual;
    public NCPharmaLabel Label;
    public NCPharmaQcState QcState = NCPharmaQcState.Untested;
    public NCPharmaQcReport? LastQc;
    public float ModifierToxicity = 1f;
    public string Producer = string.Empty;
    public string Facility = string.Empty;
    public NCActiveWorldTime CreatedAt;
    public int DosesProduced;
    public int DosesPacked;
    public int DosesAdministered;
    public readonly List<NCPharmaAuditEntry> Audit = [];

    /// <summary>Purity at the last degradation entry in the history. Runtime only.</summary>
    public float LoggedPurity = float.NaN;
}

public readonly record struct NCPharmaDoseResult(bool Accepted, NCPharmaDoseEffect Effect);

/// <summary>
/// Owns the registry of production batches. A pack in the world stores only a batch id and the public label;
/// everything that decides what a dose really does lives here and is resolved at administration time.
/// </summary>
public sealed partial class NCPharmaBatchSystem : EntitySystem
{
    [Dependency] private NCActiveWorldTimeSystem _worldTime = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPharmaDepotSystem _depot = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;

    private readonly Dictionary<Guid, NCPharmaBatchRecord> _batches = new();
    private int _nextBatchNumber = 1;

    public IReadOnlyCollection<NCPharmaBatchRecord> Batches => _batches.Values;

    public bool TryGetBatch(Guid batchId, [NotNullWhen(true)] out NCPharmaBatchRecord? batch) =>
        _batches.TryGetValue(batchId, out batch);

    public bool TryGetTuning([NotNullWhen(true)] out NCPharmaTuningPrototype? tuning)
    {
        var defaults = _prototypes.EnumeratePrototypes<NCPharmaTuningPrototype>()
            .Where(candidate => candidate.DefaultTuning)
            .ToList();
        if (defaults.Count == 1)
        {
            tuning = defaults[0];
            return true;
        }

        if (defaults.Count > 1)
            Log.Error("More than one default ncPharmaTuning is defined.");
        tuning = null;
        return false;
    }

    /// <summary>
    /// Runs one production of a known formula. The result is a hidden batch; what the producer writes on the
    /// label is a claim taken from the production profile, not a measurement.
    /// </summary>
    public bool TryCreateBatch(
        string formulaId,
        ProtoId<NCPharmaProductionProfilePrototype>? profileId,
        NCPharmaProductionInputs inputs,
        string producer,
        string facility,
        [NotNullWhen(true)] out NCPharmaBatchRecord? batch)
    {
        batch = null;
        if (!CanCreateBatch(formulaId, profileId, inputs, out var formula, out var profile, out var modifiers, out var tuning))
            return false;

        var outcome = NCPharmaBatchMath.Produce(formula, modifiers, profile, inputs);
        if (outcome.Doses < 1)
            return false;

        var record = new NCPharmaBatchRecord
        {
            BatchId = Guid.NewGuid(),
            BatchCode = $"NC-{_nextBatchNumber++:0000}",
            Formula = formulaId,
            FormulaVersion = formula.Version,
            Actual = outcome.Quality,
            ModifierToxicity = outcome.ModifierToxicity,
            Producer = producer,
            Facility = facility,
            CreatedAt = Now(),
            DosesProduced = outcome.Doses,
        };
        record.Label = new NCPharmaLabel(
            record.BatchCode,
            formulaId,
            formula.Version,
            formula.TargetConcentration,
            profile.LabelPurity,
            profile.LabelSterile,
            profile.LabelGrade);
        AddAudit(record, tuning, NCPharmaAuditKind.Created, producer,
            $"formula={formulaId} v{formula.Version} profile={profile.ID} doses={outcome.Doses} " +
            $"actual(conc={outcome.Quality.Concentration:F2} purity={outcome.Quality.Purity:F3} " +
            $"sterility={outcome.Quality.Sterility:F3} homogeneity={outcome.Quality.Homogeneity:F3} " +
            $"stability={outcome.Quality.Stability:F3}) inputs(units={inputs.InputUnits:F1} " +
            $"purity={inputs.InputPurity:F3} sterility={inputs.EquipmentSterility:F3} " +
            $"mixing={inputs.EquipmentMixing:F3} calibration={inputs.CalibrationError:F3})");
        _batches.Add(record.BatchId, record);
        if (IsTracked(formula))
            AddJournal(NCPharmaJournalKind.Produced, formulaId, record.BatchCode, producer,
                $"{outcome.Doses} doses, label purity {profile.LabelPurity:P0}");
        batch = record;
        return true;
    }

    /// <summary>
    /// Packs doses of a batch into a physical container. The container never exceeds what was produced.
    /// </summary>
    public EntityUid? TrySpawnContainer(
        EntProtoId prototype,
        EntityCoordinates coordinates,
        Guid batchId,
        int doses,
        string actor)
    {
        if (doses < 1 ||
            !_batches.TryGetValue(batchId, out var batch) ||
            doses > batch.DosesProduced - batch.DosesPacked ||
            !TryGetTuning(out var tuning))
        {
            return null;
        }

        var container = Spawn(prototype, coordinates);
        if (!TryComp<NCPharmaContainerComponent>(container, out var component))
        {
            QueueDel(container);
            return null;
        }

        batch.DosesPacked += doses;
        component.BatchId = batchId;
        component.Doses = doses;
        component.QcState = batch.QcState;
        component.KeepCold = TryGetFormula(batch.Formula, out var packFormula) &&
                             tuning.StableThreshold > packFormula.BaseStability;
        ApplyLabel(component, batch.Label);
        Dirty(container, component);
        AddAudit(batch, tuning, NCPharmaAuditKind.Dispensed, actor, $"container={ToPrettyString(container)} doses={doses}");
        return container;
    }

    /// <summary>
    /// Administers exactly one dose. The physical effect is resolved here from the hidden batch data, so the
    /// label can lie without the physiology ever being told what the label said.
    /// </summary>
    public bool TryAdministerDose(
        EntityUid administrator,
        EntityUid patient,
        Entity<NCPharmaContainerComponent> container,
        out NCPharmaDoseResult result)
    {
        result = default;
        if (!CanAdminister(patient, container, out var batch, out var formula, out var form, out var tuning))
            return false;

        var doseIndex = batch.DosesAdministered++;
        var effect = NCPharmaBatchMath.ResolveDose(
            tuning, form, batch.Actual, batch.ModifierToxicity, batch.BatchId, doseIndex);
        _depot.TryDeposit(
            patient,
            formula.Compound,
            effect.EffectiveConcentration,
            NCPharmaBatchMath.ResolveAbsorption(form, ResolveModifiers(formula)));
        if (effect.ToxinBurden > 0f)
            _systemic.Apply(patient, tuning.ToxinProfile, effect.ToxinBurden);
        if (effect.SepsisBurden > 0f)
            _systemic.Apply(patient, tuning.SepsisProfile, effect.SepsisBurden);

        container.Comp.Doses--;
        Dirty(container.Owner, container.Comp);
        if (IsTracked(formula))
            AddJournal(NCPharmaJournalKind.Administered, batch.Formula, batch.BatchCode, ToPrettyString(administrator),
                $"dose to {Name(patient)}");
        AddAudit(batch, tuning, NCPharmaAuditKind.Administered, ToPrettyString(administrator),
            $"patient={ToPrettyString(patient)} dose#{doseIndex} effective={effect.EffectiveConcentration:F2} " +
            $"toxin={effect.ToxinBurden:F2} sepsis={effect.SepsisBurden:F2}");
        result = new NCPharmaDoseResult(true, effect);
        return true;
    }

    public bool CanAdminister(
        EntityUid patient,
        Entity<NCPharmaContainerComponent> container,
        [NotNullWhen(true)] out NCPharmaBatchRecord? batch,
        [NotNullWhen(true)] out NCPharmaFormulaPrototype? formula,
        [NotNullWhen(true)] out NCPharmaDeliveryFormPrototype? form,
        [NotNullWhen(true)] out NCPharmaTuningPrototype? tuning)
    {
        formula = null;
        form = null;
        tuning = null;
        if (container.Comp.Doses < 1 ||
            !HasComp<NCMedicalBodyComponent>(patient) ||
            !_batches.TryGetValue(container.Comp.BatchId, out batch) ||
            !TryGetFormula(batch.Formula, out formula) ||
            !_prototypes.TryIndex(formula.DeliveryForm, out form) ||
            !TryGetTuning(out tuning))
        {
            batch = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Measures the requested parameters against the label. Only the tests that were performed reveal values.
    /// </summary>
    public bool TryPerformQc(
        Guid batchId,
        string actor,
        NCPharmaQcTests tests,
        out NCPharmaQcReport report)
    {
        report = default;
        if (tests == NCPharmaQcTests.None ||
            !_batches.TryGetValue(batchId, out var batch) ||
            !TryGetTuning(out var tuning))
        {
            return false;
        }

        report = NCPharmaBatchMath.RunQc(tuning, batchId, batch.Actual, batch.Label, tests);
        batch.LastQc = report;
        batch.QcState = NCPharmaBatchMath.ResolveQcState(batch.QcState, report);
        AddAudit(batch, tuning, NCPharmaAuditKind.QcPerformed, actor,
            $"tests={report.Performed} failed={report.Failed} state={batch.QcState}");
        RefreshContainers(batch);
        if (TryGetFormula(batch.Formula, out var qcFormula) && IsTracked(qcFormula))
            AddJournal(NCPharmaJournalKind.QcPerformed, batch.Formula, batch.BatchCode, actor,
                $"tests {report.Performed}, failed {report.Failed}, now {batch.QcState}");
        return true;
    }

    /// <summary>
    /// Rewrites the public label. The real composition is untouched, which is exactly how a mislabelled or
    /// counterfeit batch comes to exist; the change is always recorded with its author.
    /// </summary>
    public bool TrySetLabel(Guid batchId, string actor, NCPharmaLabel label)
    {
        if (!_batches.TryGetValue(batchId, out var batch) ||
            !TryGetTuning(out var tuning) ||
            !float.IsFinite(label.Concentration) || label.Concentration < 0f ||
            !float.IsFinite(label.Purity) || label.Purity is < 0f or > 1f)
        {
            return false;
        }

        var previous = batch.Label;
        batch.Label = label with { BatchCode = previous.BatchCode, FormulaId = previous.FormulaId, FormulaVersion = previous.FormulaVersion };
        AddAudit(batch, tuning, NCPharmaAuditKind.LabelChanged, actor,
            $"conc {previous.Concentration:F2}->{batch.Label.Concentration:F2} " +
            $"purity {previous.Purity:F3}->{batch.Label.Purity:F3} " +
            $"sterile {previous.Sterile}->{batch.Label.Sterile} grade {previous.Grade}->{batch.Label.Grade}");

        RefreshContainers(batch);
        return true;
    }

    public bool TryRecall(Guid batchId, string actor)
    {
        if (!_batches.TryGetValue(batchId, out var batch) || !TryGetTuning(out var tuning))
            return false;

        batch.QcState = NCPharmaQcState.Recalled;
        AddAudit(batch, tuning, NCPharmaAuditKind.Recalled, actor, "batch recalled");
        RefreshContainers(batch);
        return true;
    }

    /// <summary>Records that an administrator read the hidden truth. Reading is never silent.</summary>
    public void RecordInspection(NCPharmaBatchRecord batch, string actor)
    {
        if (TryGetTuning(out var tuning))
            AddAudit(batch, tuning, NCPharmaAuditKind.AdminInspected, actor, "full hidden data viewed");
    }

    private List<NCPharmaModifierPrototype> ResolveModifiers(NCPharmaFormulaPrototype formula)
    {
        var modifiers = new List<NCPharmaModifierPrototype>(formula.Modifiers.Count);
        foreach (var modifierId in formula.Modifiers)
        {
            if (_prototypes.TryIndex(modifierId, out var modifier))
                modifiers.Add(modifier);
        }

        return modifiers;
    }

    private bool CanCreateBatch(
        string formulaId,
        ProtoId<NCPharmaProductionProfilePrototype>? profileId,
        NCPharmaProductionInputs inputs,
        [NotNullWhen(true)] out NCPharmaFormulaPrototype? formula,
        [NotNullWhen(true)] out NCPharmaProductionProfilePrototype? profile,
        out List<NCPharmaModifierPrototype> modifiers,
        [NotNullWhen(true)] out NCPharmaTuningPrototype? tuning)
    {
        profile = null;
        modifiers = [];
        if (!TryGetFormula(formulaId, out formula) ||
            !TryGetTuning(out tuning) ||
            _batches.Count >= tuning.MaximumBatches ||
            !float.IsFinite(inputs.InputUnits) || inputs.InputUnits <= 0f ||
            !float.IsFinite(inputs.InputPurity) || inputs.InputPurity is < 0f or > 1f ||
            !float.IsFinite(inputs.EquipmentSterility) || inputs.EquipmentSterility is < 0f or > 1f ||
            !float.IsFinite(inputs.EquipmentMixing) || inputs.EquipmentMixing is < 0f or > 1f ||
            !float.IsFinite(inputs.CalibrationError) ||
            !_prototypes.TryIndex(profileId ?? formula.DefaultProductionProfile, out profile))
        {
            tuning = null;
            return false;
        }

        foreach (var modifierId in formula.Modifiers)
        {
            if (!_prototypes.TryIndex(modifierId, out var modifier))
                return false;
            modifiers.Add(modifier);
        }

        return NCPharmaBatchMath.ValidateFormula(formula, modifiers);
    }

    /// <summary>Copies the public label and QC certificate of a batch onto every pack that carries it.</summary>
    private void RefreshContainers(NCPharmaBatchRecord batch)
    {
        var query = EntityQueryEnumerator<NCPharmaContainerComponent>();
        while (query.MoveNext(out var uid, out var container))
        {
            if (container.BatchId != batch.BatchId)
                continue;

            ApplyLabel(container, batch.Label);
            container.QcState = batch.QcState;
            Dirty(uid, container);
        }
    }

    private static void ApplyLabel(NCPharmaContainerComponent container, NCPharmaLabel label)
    {
        container.BatchCode = label.BatchCode;
        container.FormulaId = label.FormulaId;
        container.FormulaVersion = label.FormulaVersion;
        container.LabelConcentration = label.Concentration;
        container.LabelPurity = label.Purity;
        container.LabelSterile = label.Sterile;
        container.LabelGrade = label.Grade;
    }

    /// <summary>
    /// Monotonic active-world time, which keeps advancing across rounds. Before the world has started it is zero.
    /// </summary>
    private NCActiveWorldTime Now() =>
        _worldTime.TryGetCurrentTime(out _, out var time) ? time : NCActiveWorldTime.Zero;

    private void AddAudit(
        NCPharmaBatchRecord batch,
        NCPharmaTuningPrototype tuning,
        NCPharmaAuditKind kind,
        string actor,
        string detail)
    {
        // The oldest entries go first, but the creation record is the root of every investigation and stays.
        if (batch.Audit.Count >= tuning.MaximumAuditEntriesPerBatch && batch.Audit.Count > 1)
            batch.Audit.RemoveAt(1);
        batch.Audit.Add(new NCPharmaAuditEntry(Now(), kind, actor, detail));
    }
}
