// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Server.Database;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Batches are durable world facts: a bad batch must still be traceable after a restart. The registry is
/// captured into the common end-of-round snapshot and restored at the load boundary from the same committed
/// snapshot the rest of the world uses, so a pack and its batch can never come from different checkpoints.
/// </summary>
public sealed partial class NCPharmaBatchSystem
{
    public const string SnapshotSectionType = "pharma-batches";
    public const string FormulaSectionType = "pharma-formulas";
    public const string JournalSectionType = "pharma-journal";
    private const string SnapshotOwnerType = "world";
    private const string SnapshotOwnerId = "city";
    private const int SnapshotFormatVersion = 1;
    private const int BatchesPerChunk = 32;
    private const int PersistedAuditEntries = 96;
    private const int MaximumChunkBytes = 4 * 1024 * 1024;
    private const int MaximumTextLength = 512;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCRoundRestoreContextSystem _restore = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnRoundSnapshotCapture);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(
            OnSessionStarted,
            after: [typeof(NCRoundRestoreContextSystem)]);
    }

    private void OnRoundSnapshotCapture(NCRoundSnapshotCaptureEvent args)
    {
        CaptureFormulas(args);
        CaptureJournal(args);
        CaptureKnowledge(args);
        CaptureUnknown(args);

        var ordered = _batches.Values.OrderBy(batch => batch.CreatedAt).ThenBy(batch => batch.BatchId).ToList();
        for (var start = 0; start < ordered.Count || start == 0; start += BatchesPerChunk)
        {
            var chunk = ordered.Skip(start).Take(BatchesPerChunk).Select(ToSnapshot).ToArray();
            var payload = JsonSerializer.Serialize(chunk);
            if (Encoding.UTF8.GetByteCount(payload) > MaximumChunkBytes)
            {
                args.Fail("Pharmaceutical batch chunk exceeds the section size limit.");
                return;
            }

            args.TryAddSection(
                SnapshotSectionType,
                SnapshotOwnerType,
                SnapshotOwnerId,
                SnapshotFormatVersion,
                chunk.Length,
                payload,
                start / BatchesPerChunk);
            if (ordered.Count == 0)
                break;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _batches.Clear();
        _derived.Clear();
        _journal.Clear();
        _knowledge.Clear();
        _unknownAssignments.Clear();
        _worldSeed = 0;
        _nextBatchNumber = 1;
        if (_restore.SelectionFailed)
        {
            Log.Error("Pharmaceutical batches were not restored: committed snapshot selection failed.");
            return;
        }

        if (_restore.Manifest?.SnapshotId is not { } snapshotId)
            return;

        try
        {
            var read = _database.GetNCRoundSnapshotSectionsAsync(
                    snapshotId, SnapshotSectionType, SnapshotOwnerType, SnapshotOwnerId)
                .WaitAsync(TimeSpan.FromSeconds(30));
            _tasks.BlockWaitOnTask(read);
            // Formulas first: a batch is only valid if the version it was made from exists.
            RestoreFormulas(snapshotId);
            Restore(read.GetAwaiter().GetResult());
            RestoreJournal(snapshotId);
            RestoreKnowledge(snapshotId);
            RestoreUnknown(snapshotId);
        }
        catch (Exception exception)
        {
            _batches.Clear();
            Log.Error($"Pharmaceutical batches of snapshot {snapshotId} could not be restored: {exception}");
        }
    }

    private void Restore(IReadOnlyList<NCRoundSnapshotSectionData> sections)
    {
        if (!TryGetTuning(out var tuning))
        {
            Log.Error("Pharmaceutical batches were not restored: no default ncPharmaTuning.");
            return;
        }

        var expectedChunk = 0;
        var skipped = 0;
        foreach (var section in sections.OrderBy(value => value.ChunkIndex))
        {
            if (section.FormatVersion != SnapshotFormatVersion ||
                section.ChunkIndex != expectedChunk++ ||
                Encoding.UTF8.GetByteCount(section.Payload) > MaximumChunkBytes ||
                NCRoundSnapshotCaptureEvent.Hash(section.Payload) != section.Checksum)
            {
                throw new InvalidOperationException("Invalid pharmaceutical batch checkpoint section.");
            }

            var records = JsonSerializer.Deserialize<NCPharmaBatchSnapshot[]>(section.Payload) ??
                          throw new InvalidOperationException("Missing pharmaceutical batch records.");
            if (records.Length != section.EntityCount)
                throw new InvalidOperationException("Pharmaceutical batch checkpoint count mismatch.");

            foreach (var snapshot in records)
            {
                if (_batches.Count >= tuning.MaximumBatches ||
                    !TryFromSnapshot(snapshot, out var batch) ||
                    !_batches.TryAdd(batch.BatchId, batch))
                {
                    skipped++;
                    continue;
                }

                if (TryParseBatchNumber(batch.BatchCode, out var number) && number >= _nextBatchNumber)
                    _nextBatchNumber = number + 1;
            }
        }

        if (skipped > 0)
            Log.Error($"{skipped} invalid or duplicate pharmaceutical batch records were skipped on restore.");
        Log.Info($"Restored {_batches.Count} pharmaceutical batches.");
    }

    private NCPharmaBatchSnapshot ToSnapshot(NCPharmaBatchRecord batch)
    {
        // The creation record is the root of any investigation, so it is always kept ahead of the newest entries.
        var audit = batch.Audit.Count <= PersistedAuditEntries
            ? batch.Audit
            : batch.Audit.Take(1).Concat(batch.Audit.Skip(batch.Audit.Count - (PersistedAuditEntries - 1))).ToList();
        return new NCPharmaBatchSnapshot
        {
            BatchId = batch.BatchId,
            BatchCode = batch.BatchCode,
            Formula = batch.Formula,
            FormulaVersion = batch.FormulaVersion,
            Concentration = batch.Actual.Concentration,
            Purity = batch.Actual.Purity,
            Sterility = batch.Actual.Sterility,
            Homogeneity = batch.Actual.Homogeneity,
            Stability = batch.Actual.Stability,
            LabelConcentration = batch.Label.Concentration,
            LabelPurity = batch.Label.Purity,
            LabelSterile = batch.Label.Sterile,
            LabelGrade = batch.Label.Grade,
            QcState = batch.QcState,
            QcPerformed = batch.LastQc?.Performed,
            QcFailed = batch.LastQc?.Failed,
            ModifierToxicity = batch.ModifierToxicity,
            Masking = batch.Masking,
            Producer = batch.Producer,
            Facility = batch.Facility,
            CreatedTicks = batch.CreatedAt.Ticks,
            DosesProduced = batch.DosesProduced,
            DosesPacked = batch.DosesPacked,
            DosesAdministered = batch.DosesAdministered,
            Audit = audit
                .Select(entry => new NCPharmaAuditSnapshot
                {
                    Ticks = entry.Time.Ticks,
                    Kind = entry.Kind,
                    Actor = entry.Actor,
                    Detail = entry.Detail,
                })
                .ToList(),
        };
    }

    private bool TryFromSnapshot(NCPharmaBatchSnapshot value, out NCPharmaBatchRecord batch)
    {
        batch = default!;
        if (value.BatchId == Guid.Empty ||
            !IsText(value.BatchCode, 1) ||
            !IsText(value.Formula, 1) ||
            !TryGetFormula(value.Formula, out _) ||
            value.FormulaVersion < 1 ||
            !IsRange(value.Concentration, 0f, 1000f) ||
            !IsRange(value.Purity, 0f, 1f) ||
            !IsRange(value.Sterility, 0f, 1f) ||
            !IsRange(value.Homogeneity, 0f, 1f) ||
            !IsRange(value.Stability, 0f, 1f) ||
            !IsRange(value.LabelConcentration, 0f, 1000f) ||
            !IsRange(value.LabelPurity, 0f, 1f) ||
            !IsRange(value.ModifierToxicity, 0f, 100f) ||
            !IsRange(value.Masking, 0f, 1f) ||
            !Enum.IsDefined(value.LabelGrade) ||
            !Enum.IsDefined(value.QcState) ||
            value.DosesProduced < 0 ||
            value.DosesPacked < 0 || value.DosesPacked > value.DosesProduced ||
            value.DosesAdministered < 0 || value.DosesAdministered > value.DosesPacked ||
            value.CreatedTicks < 0 ||
            !IsText(value.Producer, 0) || !IsText(value.Facility, 0) ||
            value.Audit == null || value.Audit.Count > PersistedAuditEntries)
        {
            return false;
        }

        var audit = new List<NCPharmaAuditEntry>(value.Audit.Count);
        foreach (var entry in value.Audit)
        {
            if (entry.Ticks < 0 || !Enum.IsDefined(entry.Kind) || !IsText(entry.Actor, 0) || !IsText(entry.Detail, 0))
                return false;
            audit.Add(new NCPharmaAuditEntry(new NCActiveWorldTime(entry.Ticks), entry.Kind, entry.Actor, entry.Detail));
        }

        var actual = new NCPharmaQuality(
            value.Concentration, value.Purity, value.Sterility, value.Homogeneity, value.Stability);
        batch = new NCPharmaBatchRecord
        {
            BatchId = value.BatchId,
            BatchCode = value.BatchCode,
            Formula = value.Formula,
            FormulaVersion = value.FormulaVersion,
            Actual = actual,
            Label = new NCPharmaLabel(
                value.BatchCode,
                value.Formula,
                value.FormulaVersion,
                value.LabelConcentration,
                value.LabelPurity,
                value.LabelSterile,
                value.LabelGrade),
            QcState = value.QcState,
            ModifierToxicity = value.ModifierToxicity,
            Masking = value.Masking,
            Producer = value.Producer,
            Facility = value.Facility,
            CreatedAt = new NCActiveWorldTime(value.CreatedTicks),
            DosesProduced = value.DosesProduced,
            DosesPacked = value.DosesPacked,
            DosesAdministered = value.DosesAdministered,
        };
        batch.Audit.AddRange(audit);
        return true;
    }

    private void CaptureFormulas(NCRoundSnapshotCaptureEvent args)
    {
        var records = _derived.Values
            .OrderBy(formula => formula.ID, StringComparer.Ordinal)
            .Select(formula => new NCPharmaFormulaSnapshot
            {
                Id = formula.ID,
                Name = formula.Name.Id,
                Version = formula.Version,
                Compound = formula.Compound.Id,
                DeliveryForm = formula.DeliveryForm.Id,
                Target = formula.TargetConcentration,
                Modifiers = formula.Modifiers.Select(modifier => modifier.Id).ToList(),
                Profile = formula.DefaultProductionProfile.Id,
                Stability = formula.BaseStability,
                Status = formula.RegulatoryStatus,
                DerivedFrom = formula.DerivedFrom,
                CustomName = formula.CustomName,
                Author = formula.Author,
                Reaction = formula.Reaction?.Id,
            })
            .ToArray();
        AddSimpleSection(args, FormulaSectionType, records);
    }

    private void CaptureJournal(NCRoundSnapshotCaptureEvent args)
    {
        var records = _journal
            .Select(entry => new NCPharmaJournalSnapshot
            {
                Ticks = entry.Time.Ticks,
                Kind = entry.Kind,
                FormulaId = entry.FormulaId,
                BatchCode = entry.BatchCode,
                Actor = entry.Actor,
                Detail = entry.Detail,
            })
            .ToArray();
        AddSimpleSection(args, JournalSectionType, records);
    }

    private static void AddSimpleSection<T>(NCRoundSnapshotCaptureEvent args, string sectionType, T[] records)
    {
        var payload = JsonSerializer.Serialize(records);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumChunkBytes)
        {
            args.Fail($"Pharmaceutical section {sectionType} exceeds the section size limit.");
            return;
        }

        args.TryAddSection(sectionType, SnapshotOwnerType, SnapshotOwnerId, SnapshotFormatVersion, records.Length, payload);
    }

    private T[] ReadSimpleSection<T>(Guid snapshotId, string sectionType)
    {
        var read = _database.GetNCRoundSnapshotSectionsAsync(snapshotId, sectionType, SnapshotOwnerType, SnapshotOwnerId)
            .WaitAsync(TimeSpan.FromSeconds(30));
        _tasks.BlockWaitOnTask(read);
        var sections = read.GetAwaiter().GetResult();
        if (sections.Count == 0)
            return [];
        if (sections.Count != 1)
            throw new InvalidOperationException($"Pharmaceutical section {sectionType} has {sections.Count} chunks.");

        var section = sections[0];
        if (section.FormatVersion != SnapshotFormatVersion ||
            section.ChunkIndex != 0 ||
            Encoding.UTF8.GetByteCount(section.Payload) > MaximumChunkBytes ||
            NCRoundSnapshotCaptureEvent.Hash(section.Payload) != section.Checksum)
        {
            throw new InvalidOperationException($"Invalid pharmaceutical section {sectionType}.");
        }

        var records = JsonSerializer.Deserialize<T[]>(section.Payload) ??
                      throw new InvalidOperationException($"Missing pharmaceutical section {sectionType}.");
        if (records.Length != section.EntityCount)
            throw new InvalidOperationException($"Pharmaceutical section {sectionType} count mismatch.");
        return records;
    }

    private void RestoreFormulas(Guid snapshotId)
    {
        if (!TryGetTuning(out var tuning))
            return;

        var skipped = 0;
        foreach (var record in ReadSimpleSection<NCPharmaFormulaSnapshot>(snapshotId, FormulaSectionType))
        {
            if (_derived.Count >= tuning.MaximumDerivedFormulas ||
                !TryFormulaFromSnapshot(record, out var formula) ||
                !_derived.TryAdd(formula.ID, formula))
            {
                skipped++;
            }
        }

        if (skipped > 0)
            Log.Error($"{skipped} invalid or duplicate pharmaceutical formulas were skipped on restore.");
    }

    private void RestoreJournal(Guid snapshotId)
    {
        if (!TryGetTuning(out var tuning))
            return;

        foreach (var record in ReadSimpleSection<NCPharmaJournalSnapshot>(snapshotId, JournalSectionType))
        {
            if (_journal.Count >= tuning.MaximumJournalEntries)
                break;
            if (record.Ticks < 0 || !Enum.IsDefined(record.Kind) ||
                !IsText(record.FormulaId, 1) || !IsText(record.BatchCode, 0) ||
                !IsText(record.Actor, 0) || !IsText(record.Detail, 0))
            {
                continue;
            }

            _journal.Add(new NCPharmaJournalEntry(
                new NCActiveWorldTime(record.Ticks), record.Kind, record.FormulaId, record.BatchCode, record.Actor, record.Detail));
        }
    }

    private bool TryFormulaFromSnapshot(NCPharmaFormulaSnapshot value, out NCPharmaFormulaPrototype formula)
    {
        formula = default!;
        if (!IsText(value.Id, 1) || !IsText(value.Name, 1) ||
            value.Version < 1 ||
            !_prototypes.HasIndex<NCDrugProfilePrototype>(value.Compound) ||
            !_prototypes.HasIndex<NCPharmaDeliveryFormPrototype>(value.DeliveryForm) ||
            !_prototypes.HasIndex<NCPharmaProductionProfilePrototype>(value.Profile) ||
            !IsRange(value.Target, 0.0001f, 1000f) ||
            !IsRange(value.Stability, 0f, 1f) ||
            !Enum.IsDefined(value.Status) ||
            value.Modifiers == null || value.Modifiers.Count > NCPharmaBatchMath.MaximumModifiers ||
            value.Modifiers.Any(modifier => !_prototypes.HasIndex<NCPharmaModifierPrototype>(modifier)) ||
            (value.DerivedFrom != null && !IsText(value.DerivedFrom, 1)) ||
            (value.CustomName != null && !IsText(value.CustomName, 1)) ||
            (value.Author != null && !IsText(value.Author, 0)) ||
            (value.Reaction != null && !_prototypes.HasIndex<NCPharmaReactionProfilePrototype>(value.Reaction)))
        {
            return false;
        }

        formula = NCPharmaFormulaPrototype.Create(
            value.Id,
            value.Name,
            value.Version,
            value.Compound,
            value.DeliveryForm,
            value.Target,
            [.. value.Modifiers.Select(modifier => new ProtoId<NCPharmaModifierPrototype>(modifier))],
            value.Profile,
            value.Stability,
            value.Status,
            value.DerivedFrom,
            value.CustomName,
            value.Author,
            value.Reaction == null ? (ProtoId<NCPharmaReactionProfilePrototype>?) null : new ProtoId<NCPharmaReactionProfilePrototype>(value.Reaction));
        return true;
    }

    private static bool TryParseBatchNumber(string code, out int number)
    {
        number = 0;
        return code.StartsWith("NC-", StringComparison.Ordinal) &&
               int.TryParse(code.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsText(string? value, int minimum) =>
        value != null && value.Length >= minimum && value.Length <= MaximumTextLength;

    private static bool IsRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>Plain serialization shape of one batch. It carries no entity reference, only semantic data.</summary>
public sealed class NCPharmaBatchSnapshot
{
    public Guid BatchId { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
    public int FormulaVersion { get; set; }
    public float Concentration { get; set; }
    public float Purity { get; set; }
    public float Sterility { get; set; }
    public float Homogeneity { get; set; }
    public float Stability { get; set; }
    public float LabelConcentration { get; set; }
    public float LabelPurity { get; set; }
    public bool LabelSterile { get; set; }
    public NCPharmaGrade LabelGrade { get; set; }
    public NCPharmaQcState QcState { get; set; }
    public NCPharmaQcTests? QcPerformed { get; set; }
    public NCPharmaQcTests? QcFailed { get; set; }
    public float ModifierToxicity { get; set; } = 1f;
    public float Masking { get; set; }
    public string Producer { get; set; } = string.Empty;
    public string Facility { get; set; } = string.Empty;
    public long CreatedTicks { get; set; }
    public int DosesProduced { get; set; }
    public int DosesPacked { get; set; }
    public int DosesAdministered { get; set; }
    public List<NCPharmaAuditSnapshot> Audit { get; set; } = [];
}

public sealed class NCPharmaAuditSnapshot
{
    public long Ticks { get; set; }
    public NCPharmaAuditKind Kind { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class NCPharmaFormulaSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Compound { get; set; } = string.Empty;
    public string DeliveryForm { get; set; } = string.Empty;
    public float Target { get; set; }
    public List<string> Modifiers { get; set; } = [];
    public string Profile { get; set; } = string.Empty;
    public float Stability { get; set; }
    public NCPharmaRegulatoryStatus Status { get; set; }
    public string? DerivedFrom { get; set; }
    public string? CustomName { get; set; }
    public string? Author { get; set; }
    public string? Reaction { get; set; }
}

public sealed class NCPharmaJournalSnapshot
{
    public long Ticks { get; set; }
    public NCPharmaJournalKind Kind { get; set; }
    public string FormulaId { get; set; } = string.Empty;
    public string BatchCode { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
