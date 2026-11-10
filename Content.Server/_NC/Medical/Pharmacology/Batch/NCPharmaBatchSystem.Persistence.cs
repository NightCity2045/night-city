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
            Restore(read.GetAwaiter().GetResult());
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
            Formula = batch.Formula.Id,
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
            !_prototypes.HasIndex<NCPharmaFormulaPrototype>(value.Formula) ||
            value.FormulaVersion < 1 ||
            !IsRange(value.Concentration, 0f, 1000f) ||
            !IsRange(value.Purity, 0f, 1f) ||
            !IsRange(value.Sterility, 0f, 1f) ||
            !IsRange(value.Homogeneity, 0f, 1f) ||
            !IsRange(value.Stability, 0f, 1f) ||
            !IsRange(value.LabelConcentration, 0f, 1000f) ||
            !IsRange(value.LabelPurity, 0f, 1f) ||
            !IsRange(value.ModifierToxicity, 0f, 100f) ||
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
