// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// What the laboratory has learned about each compound. Knowledge is a fact about the world, not a roll: once a
/// property is confirmed it stays confirmed, and it survives restarts with the rest of the pharmacopoeia.
/// </summary>
public sealed partial class NCPharmaBatchSystem
{
    public const string KnowledgeSectionType = "pharma-knowledge";

    private readonly Dictionary<ProtoId<NCDrugProfilePrototype>, NCAnalysisTier> _knowledge = new();

    /// <summary>The deepest methods that have been run on a compound.</summary>
    public NCAnalysisTier GetKnowledge(ProtoId<NCDrugProfilePrototype> compound)
    {
        return _knowledge.GetValueOrDefault(compound);
    }

    /// <summary>A compound with no codename is a known substance; one with a codename must be analysed first.</summary>
    public bool IsIdentified(ProtoId<NCDrugProfilePrototype> compound)
    {
        return !_prototypes.TryIndex(compound, out var drug) ||
               drug.Codename == null ||
               (GetKnowledge(compound) & NCAnalysisTier.Chemical) != 0;
    }

    /// <summary>The compound of the batch a pack holds. False for a pack of an unknown or recalled batch.</summary>
    public bool TryGetPackCompound(NCPharmaContainerComponent pack, out ProtoId<NCDrugProfilePrototype> compound)
    {
        compound = default;
        if (!_batches.TryGetValue(pack.BatchId, out var batch) || !TryGetFormula(batch.Formula, out var formula))
            return false;

        compound = formula.Compound;
        return true;
    }

    /// <summary>How much of a batch's impurity a simple QC run cannot see. Only a compound study reveals it.</summary>
    public float GetPackMasking(NCPharmaContainerComponent pack)
    {
        return _batches.TryGetValue(pack.BatchId, out var batch) ? batch.Masking : 0f;
    }

    /// <summary>Records a finished analysis and returns the knowledge after it.</summary>
    public NCAnalysisTier RecordAnalysis(ProtoId<NCDrugProfilePrototype> compound, NCAnalysisTier performed)
    {
        if (!_prototypes.HasIndex(compound))
            return NCAnalysisTier.None;

        var updated = NCCompoundDossierMath.Include(GetKnowledge(compound), performed);
        _knowledge[compound] = updated;
        return updated;
    }

    /// <summary>The compound's dossier as far as the laboratory knows it.</summary>
    public List<NCCompoundPropertyReading> ReadDossier(ProtoId<NCDrugProfilePrototype> compound)
    {
        if (!_prototypes.TryIndex(compound, out var drug))
            return [];

        NCDependenceProfilePrototype? dependence = null;
        if (drug.Dependence is { } dependenceId)
            _prototypes.TryIndex(dependenceId, out dependence);

        return NCCompoundDossierMath.Read(NCCompoundDossierMath.Build(drug, dependence), GetKnowledge(compound));
    }

    /// <summary>Localization key of how a compound is called in a report: its codename until it is identified.</summary>
    public string CompoundNameKey(ProtoId<NCDrugProfilePrototype> compound)
    {
        return IsIdentified(compound) || !_prototypes.TryIndex(compound, out var drug) || drug.Codename == null
            ? NCLabText.DrugKey(compound.Id)
            : drug.Codename.Value;
    }

    /// <summary>
    /// Administrative event: the real composition of a batch is changed after the fact, for example to stage a
    /// contaminated delivery. The batch keeps its identity and the change is written to its history; packs that
    /// were already dispensed are affected too because they only point at the batch.
    /// </summary>
    public bool TryAdminAdjust(Guid batchId, string actor, float? purity, float? sterility)
    {
        if (!_batches.TryGetValue(batchId, out var batch) || !TryGetTuning(out var tuning))
            return false;

        var quality = batch.Actual;
        if (purity is { } newPurity && float.IsFinite(newPurity))
            quality = quality with { Purity = Math.Clamp(newPurity, 0f, 1f) };
        if (sterility is { } newSterility && float.IsFinite(newSterility))
            quality = quality with { Sterility = Math.Clamp(newSterility, 0f, 1f) };

        batch.Actual = quality;
        AddAudit(batch, tuning, NCPharmaAuditKind.AdminAdjusted, actor,
            $"purity {quality.Purity:P1}, sterility {quality.Sterility:P1}");
        RefreshContainers(batch);
        return true;
    }

    private void CaptureKnowledge(NCRoundSnapshotCaptureEvent args)
    {
        var records = _knowledge
            .Where(entry => entry.Value != NCAnalysisTier.None)
            .OrderBy(entry => entry.Key.Id, StringComparer.Ordinal)
            .Select(entry => new NCPharmaKnowledgeSnapshot { Compound = entry.Key.Id, Tier = (byte) entry.Value })
            .ToArray();
        AddSimpleSection(args, KnowledgeSectionType, records);
    }

    private void RestoreKnowledge(Guid snapshotId)
    {
        foreach (var record in ReadSimpleSection<NCPharmaKnowledgeSnapshot>(snapshotId, KnowledgeSectionType))
        {
            if (!_prototypes.HasIndex<NCDrugProfilePrototype>(record.Compound) || record.Tier > 7)
                continue;

            _knowledge[record.Compound] = (NCAnalysisTier) record.Tier;
        }
    }
}

public sealed class NCPharmaKnowledgeSnapshot
{
    public string Compound { get; set; } = string.Empty;
    public byte Tier { get; set; }
}
