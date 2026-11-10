// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

public enum NCPharmaExperimentKind : byte
{
    /// <summary>An experimental batch was made. The process settings and what came out are recorded.</summary>
    Production,

    /// <summary>A batch was studied on a test subject in the vivarium.</summary>
    Vivarium,
}

public enum NCPharmaNoteSection : byte
{
    Process,
    Observation,
    Analysis,
}

public enum NCPharmaNoteKind : byte
{
    Profile,
    Units,
    InputPurity,
    Output,
    QcResult,
    SubjectOutcome,
    Peak,
    ToxicRatio,
    Toxin,
    Sepsis,
    Clinical,
}

/// <summary>One recorded fact of an experiment. Text carries a name or an id; the numbers carry the rest.</summary>
public sealed record NCPharmaExperimentNote(
    NCPharmaNoteSection Section,
    NCPharmaNoteKind Kind,
    float A = 0f,
    float B = 0f,
    string Text = "");

/// <summary>
/// The record of one experiment: what formula version it concerned, what was put in, how the process was set,
/// what came out, what was seen and measured, who was tested, who did it and when.
/// </summary>
public sealed class NCPharmaExperimentRecord
{
    public string ExperimentId = string.Empty;
    public NCPharmaExperimentKind Kind;
    public string FormulaId = string.Empty;
    public int FormulaVersion;
    public List<Guid> InputBatchIds = [];
    public Guid? OutputBatchId;
    public List<NCPharmaExperimentNote> Notes = [];
    public List<string> TestedSubjects = [];
    public string Author = string.Empty;
    public NCActiveWorldTime CreatedAt;
}

public sealed partial class NCPharmaBatchSystem
{
    public const string ExperimentSectionType = "pharma-experiments";

    private readonly List<NCPharmaExperimentRecord> _experiments = [];
    private int _nextExperiment = 1;

    public IReadOnlyList<NCPharmaExperimentRecord> Experiments => _experiments;

    private NCPharmaExperimentRecord? StartExperiment(
        NCPharmaExperimentKind kind,
        NCPharmaFormulaPrototype formula,
        string author)
    {
        if (!TryGetTuning(out var tuning) || _experiments.Count >= tuning.MaximumExperiments)
            return null;

        var record = new NCPharmaExperimentRecord
        {
            ExperimentId = $"EXP-{_nextExperiment++:D4}",
            Kind = kind,
            FormulaId = formula.ID,
            FormulaVersion = formula.Version,
            Author = author,
            CreatedAt = Now(),
        };
        _experiments.Add(record);
        return record;
    }

    private void AddNote(NCPharmaExperimentRecord record, NCPharmaExperimentNote note)
    {
        if (TryGetTuning(out var tuning) && record.Notes.Count < tuning.MaximumExperimentNotes)
            record.Notes.Add(note);
    }

    /// <summary>An experimental formula is being produced: record the settings, so the result can be explained.</summary>
    private void RecordProduction(
        NCPharmaFormulaPrototype formula,
        NCPharmaBatchRecord batch,
        string profileId,
        NCPharmaProductionInputs inputs,
        string producer)
    {
        if (formula.RegulatoryStatus != NCPharmaRegulatoryStatus.Experimental ||
            StartExperiment(NCPharmaExperimentKind.Production, formula, producer) is not { } record)
        {
            return;
        }

        record.OutputBatchId = batch.BatchId;
        AddNote(record, new(NCPharmaNoteSection.Process, NCPharmaNoteKind.Profile, Text: profileId));
        AddNote(record, new(NCPharmaNoteSection.Process, NCPharmaNoteKind.Units, inputs.InputUnits));
        AddNote(record, new(NCPharmaNoteSection.Process, NCPharmaNoteKind.InputPurity, inputs.InputPurity));
        AddNote(record, new(NCPharmaNoteSection.Observation, NCPharmaNoteKind.Output, batch.DosesProduced));
    }

    private NCPharmaExperimentRecord? FindProductionExperiment(Guid batchId)
    {
        return _experiments.LastOrDefault(record =>
            record.Kind == NCPharmaExperimentKind.Production && record.OutputBatchId == batchId);
    }

    private void RecordQcAnalysis(Guid batchId, NCPharmaQcReport report)
    {
        if (FindProductionExperiment(batchId) is { } record)
            AddNote(record, new(NCPharmaNoteSection.Analysis, NCPharmaNoteKind.QcResult, (float) report.Performed, (float) report.Failed));
    }

    private void RecordClinicalResponse(NCPharmaBatchRecord batch, string patient, float concentration)
    {
        if (FindProductionExperiment(batch.BatchId) is { } record)
            AddNote(record, new(NCPharmaNoteSection.Observation, NCPharmaNoteKind.Clinical, concentration, Text: patient));
    }

    /// <summary>
    /// Gives one dose of a pack to a test subject. The dose is spent for real; the result is what the same dose
    /// would do to a standard body, and the study is written to the experiment records.
    /// </summary>
    public bool TryStudyInVivo(
        NCPharmaContainerComponent pack,
        EntityUid packEntity,
        string actor,
        string subject,
        out NCPharmaStudyResult result)
    {
        result = default;
        if (pack.Doses < 1 ||
            !_batches.TryGetValue(pack.BatchId, out var batch) ||
            !TryGetFormula(batch.Formula, out var formula) ||
            !_prototypes.TryIndex(formula.DeliveryForm, out var form) ||
            !_prototypes.TryIndex(formula.Compound, out var compound) ||
            !TryGetTuning(out var tuning))
        {
            return false;
        }

        var doseIndex = batch.DosesAdministered++;
        var effect = NCPharmaBatchMath.ResolveDose(
            tuning, form, batch.Actual, batch.ModifierToxicity, batch.BatchId, doseIndex);
        result = NCPharmaBatchMath.ResolveStudy(tuning, effect, compound.ToxicConcentration);
        pack.Doses--;
        Dirty(packEntity, pack);
        RecordAnalysis(formula.Compound, NCAnalysisTier.InVivo);
        AddAudit(batch, tuning, NCPharmaAuditKind.Administered, actor,
            $"vivarium subject={subject} outcome={result.Outcome} peak={result.Peak:F2}");

        if (StartExperiment(NCPharmaExperimentKind.Vivarium, formula, actor) is { } record)
        {
            record.InputBatchIds.Add(batch.BatchId);
            record.TestedSubjects.Add(subject);
            AddNote(record, new(NCPharmaNoteSection.Observation, NCPharmaNoteKind.SubjectOutcome, (float) result.Outcome));
            AddNote(record, new(NCPharmaNoteSection.Analysis, NCPharmaNoteKind.Peak, result.Peak));
            AddNote(record, new(NCPharmaNoteSection.Analysis, NCPharmaNoteKind.ToxicRatio, result.ToxicRatio));
            AddNote(record, new(NCPharmaNoteSection.Analysis, NCPharmaNoteKind.Toxin, result.Toxin));
            AddNote(record, new(NCPharmaNoteSection.Analysis, NCPharmaNoteKind.Sepsis, result.Sepsis));
        }

        return true;
    }

    /// <summary>One line per experiment of a formula's lineage, for the research console.</summary>
    private IEnumerable<NCPharmaJournalSummary> BuildExperimentRows(HashSet<string> lineage)
    {
        foreach (var record in _experiments.Where(record => lineage.Contains(record.FormulaId)))
        {
            var batchCode = record.OutputBatchId is { } id && _batches.TryGetValue(id, out var batch)
                ? batch.BatchCode
                : record.InputBatchIds.Count > 0 && _batches.TryGetValue(record.InputBatchIds[0], out var studied)
                    ? studied.BatchCode
                    : string.Empty;
            yield return new NCPharmaJournalSummary(
                NCPharmaJournalKind.Experiment,
                record.FormulaId,
                batchCode,
                record.Author,
                DescribeExperiment(record));
        }
    }

    public string DescribeExperiment(NCPharmaExperimentRecord record)
    {
        var parts = new List<string>
        {
            Loc.GetString(record.Kind == NCPharmaExperimentKind.Production
                ? "nc-pharma-exp-kind-production"
                : "nc-pharma-exp-kind-vivarium"),
            $"v{record.FormulaVersion}",
        };
        foreach (var note in record.Notes)
            parts.Add(DescribeNote(note));
        if (record.TestedSubjects.Count > 0)
            parts.Add(Loc.GetString("nc-pharma-exp-subjects", ("subjects", string.Join(", ", record.TestedSubjects))));
        return $"{record.ExperimentId}: " + string.Join(" · ", parts);
    }

    private string DescribeNote(NCPharmaExperimentNote note) => note.Kind switch
    {
        NCPharmaNoteKind.Profile => Loc.GetString("nc-pharma-exp-note-profile", ("profile", note.Text)),
        NCPharmaNoteKind.Units => Loc.GetString("nc-pharma-exp-note-units", ("units", note.A.ToString("0.#"))),
        NCPharmaNoteKind.InputPurity => Loc.GetString("nc-pharma-exp-note-input-purity", ("purity", note.A.ToString("P0"))),
        NCPharmaNoteKind.Output => Loc.GetString("nc-pharma-exp-note-output", ("doses", ((int) note.A).ToString())),
        NCPharmaNoteKind.QcResult => Loc.GetString("nc-pharma-exp-note-qc",
            ("performed", ((int) note.A).ToString()), ("failed", ((int) note.B).ToString())),
        NCPharmaNoteKind.SubjectOutcome => Loc.GetString("nc-pharma-exp-note-outcome-" + OutcomeId((NCPharmaStudyOutcome) (int) note.A)),
        NCPharmaNoteKind.Peak => Loc.GetString("nc-pharma-exp-note-peak", ("peak", note.A.ToString("0.##"))),
        NCPharmaNoteKind.ToxicRatio => Loc.GetString("nc-pharma-exp-note-ratio", ("ratio", note.A.ToString("P0"))),
        NCPharmaNoteKind.Toxin => Loc.GetString("nc-pharma-exp-note-toxin", ("toxin", note.A.ToString("0.##"))),
        NCPharmaNoteKind.Sepsis => Loc.GetString("nc-pharma-exp-note-sepsis", ("sepsis", note.A.ToString("0.##"))),
        _ => Loc.GetString("nc-pharma-exp-note-clinical", ("patient", note.Text), ("peak", note.A.ToString("0.##"))),
    };

    public static string OutcomeId(NCPharmaStudyOutcome outcome) => outcome switch
    {
        NCPharmaStudyOutcome.Tolerated => "tolerated",
        NCPharmaStudyOutcome.Strained => "strained",
        _ => "lost",
    };

    private void CaptureExperiments(NCRoundSnapshotCaptureEvent args)
    {
        var records = _experiments.Select(record => new NCPharmaExperimentSnapshot
        {
            ExperimentId = record.ExperimentId,
            Kind = record.Kind,
            FormulaId = record.FormulaId,
            FormulaVersion = record.FormulaVersion,
            InputBatchIds = [.. record.InputBatchIds],
            OutputBatchId = record.OutputBatchId,
            Author = record.Author,
            Ticks = record.CreatedAt.Ticks,
            TestedSubjects = [.. record.TestedSubjects],
            Notes = record.Notes.Select(note => new NCPharmaNoteSnapshot
            {
                Section = note.Section, Kind = note.Kind, A = note.A, B = note.B, Text = note.Text,
            }).ToList(),
        }).ToArray();
        AddSimpleSection(args, ExperimentSectionType, records);
    }

    private void RestoreExperiments(Guid snapshotId)
    {
        if (!TryGetTuning(out var tuning))
            return;

        foreach (var value in ReadSimpleSection<NCPharmaExperimentSnapshot>(snapshotId, ExperimentSectionType))
        {
            if (_experiments.Count >= tuning.MaximumExperiments ||
                !Enum.IsDefined(value.Kind) || !IsText(value.ExperimentId, 1) || !IsText(value.FormulaId, 1) ||
                value.FormulaVersion < 1 || value.Ticks < 0 || value.Notes == null ||
                value.Notes.Count > tuning.MaximumExperimentNotes ||
                value.Notes.Any(note => !Enum.IsDefined(note.Section) || !Enum.IsDefined(note.Kind) ||
                                        !float.IsFinite(note.A) || !float.IsFinite(note.B) || !IsText(note.Text, 0)))
            {
                continue;
            }

            _experiments.Add(new NCPharmaExperimentRecord
            {
                ExperimentId = value.ExperimentId,
                Kind = value.Kind,
                FormulaId = value.FormulaId,
                FormulaVersion = value.FormulaVersion,
                InputBatchIds = [.. value.InputBatchIds],
                OutputBatchId = value.OutputBatchId,
                Author = value.Author,
                CreatedAt = new NCActiveWorldTime(value.Ticks),
                TestedSubjects = [.. value.TestedSubjects],
                Notes = value.Notes.Select(note => new NCPharmaExperimentNote(note.Section, note.Kind, note.A, note.B, note.Text)).ToList(),
            });
        }

        _nextExperiment = _experiments.Count + 1;
    }
}

public sealed class NCPharmaNoteSnapshot
{
    public NCPharmaNoteSection Section { get; set; }
    public NCPharmaNoteKind Kind { get; set; }
    public float A { get; set; }
    public float B { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class NCPharmaExperimentSnapshot
{
    public string ExperimentId { get; set; } = string.Empty;
    public NCPharmaExperimentKind Kind { get; set; }
    public string FormulaId { get; set; } = string.Empty;
    public int FormulaVersion { get; set; }
    public List<Guid> InputBatchIds { get; set; } = [];
    public Guid? OutputBatchId { get; set; }
    public string Author { get; set; } = string.Empty;
    public long Ticks { get; set; }
    public List<string> TestedSubjects { get; set; } = [];
    public List<NCPharmaNoteSnapshot> Notes { get; set; } = [];
}
