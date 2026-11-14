// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// What the laboratory has learned about each compound. Knowledge is a fact about the world, not a roll: once a
/// property is confirmed it stays confirmed, and it survives restarts with the rest of the pharmacopoeia.
/// </summary>
public sealed partial class NCPharmaBatchSystem
{
    public const string KnowledgeSectionType = "pharma-knowledge";

    public const string UnknownSectionType = "pharma-unknown";
    private const string SeedKey = "#seed";

    [Dependency] private IRobustRandom _random = default!;

    private readonly Dictionary<ProtoId<NCDrugProfilePrototype>, NCAnalysisTier> _knowledge = new();

    /// <summary>Which compound each kind of unknown raw material turned out to be. Fixed once, then persistent.</summary>
    private readonly Dictionary<string, (ProtoId<NCDrugProfilePrototype> Compound, string Pool)> _unknownAssignments = new();

    private uint _worldSeed;

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

    /// <summary>Contaminants of the raw material behind a batch. A compound study reveals them.</summary>
    public float GetPackContaminants(NCPharmaContainerComponent pack)
    {
        return _batches.TryGetValue(pack.BatchId, out var batch) ? batch.Contaminants : 0f;
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

    /// <summary>How a compound is called in a report: its codename until it is identified, then its name.</summary>
    public string CompoundName(ProtoId<NCDrugProfilePrototype> compound)
    {
        if (!IsIdentified(compound) && _prototypes.TryIndex(compound, out var drug) && drug.Codename != null)
            return Loc.GetString(drug.Codename.Value, ("code", drug.CodenameCode));

        return Loc.TryGetString(NCLabText.DrugKey(compound.Id), out var name) ? name : CodenameOrId(compound);
    }

    /// <summary>The codename of a generated compound, or its id for any other compound without a name.</summary>
    public string CodenameOrId(ProtoId<NCDrugProfilePrototype> compound)
    {
        return _prototypes.TryIndex(compound, out var drug) && drug.Codename != null
            ? Loc.GetString(drug.Codename.Value, ("code", drug.CodenameCode))
            : compound.Id;
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

    /// <summary>
    /// Decides what an unknown raw material is. The first time a kind of material is met, a compound is
    /// generated from the world's seed and the material; afterwards it is always the same compound, in every
    /// session. The compound is loaded as a real prototype, so all pharmacology treats it like any other.
    /// </summary>
    public bool TryResolveUnknown(string pool, string materialKey, out ProtoId<NCDrugProfilePrototype> compound)
    {
        compound = default;
        if (_unknownAssignments.TryGetValue(materialKey, out var existing))
        {
            compound = existing.Compound;
            return true;
        }

        if (_worldSeed == 0)
            _worldSeed = (uint) _random.Next(1, int.MaxValue);

        if (!LoadGenerated(materialKey, pool, out compound))
            return false;

        _unknownAssignments[materialKey] = (compound, pool);
        return true;
    }

    private bool LoadGenerated(string materialKey, string pool, out ProtoId<NCDrugProfilePrototype> compound)
    {
        compound = NCCompoundGenerator.IdFor(_worldSeed, materialKey);
        if (_prototypes.HasIndex<NCDrugProfilePrototype>(compound))
            return true;

        try
        {
            _prototypes.LoadString(NCCompoundGenerator.ToYaml(_worldSeed, materialKey, pool));
            _prototypes.ResolveResults();
        }
        catch (Exception exception)
        {
            Log.Error($"Generated compound for {materialKey} could not be loaded: {exception}");
            return false;
        }

        if (_prototypes.TryIndex(compound, out var drug) && ValidateGenerated(drug))
            return true;

        Log.Error($"Generated compound {compound} for {materialKey} is not a valid drug profile.");
        return false;
    }

    private static bool ValidateGenerated(NCDrugProfilePrototype drug) => NCPharmacologySystem.ValidateDrug(drug);

    /// <summary>
    /// Isolates the compound inside an unknown raw material and gives the laboratory an experimental formula
    /// for it, named by its codename. Isolating the same material again finds the same formula.
    /// </summary>
    public bool TryIsolateCompound(
        string pool,
        EntityUid material,
        string author,
        out NCPharmaFormulaPrototype formula,
        out string codename)
    {
        formula = default!;
        codename = string.Empty;
        var key = MetaData(material).EntityPrototype?.ID ?? string.Empty;
        if (key.Length == 0 || !TryGetTuning(out var tuning) ||
            !TryResolveUnknown(pool, key, out var compound) ||
            !_prototypes.TryIndex(compound, out var drug) || drug.Codename == null)
        {
            return false;
        }

        codename = CodenameOrId(compound);
        var id = $"NCPharmaFormulaIsolated.{compound.Id}";
        if (_derived.TryGetValue(id, out var existing))
        {
            formula = existing;
            return true;
        }

        if (_derived.Count >= tuning.MaximumDerivedFormulas)
            return false;

        formula = NCPharmaFormulaPrototype.Create(
            id,
            "nc-pharma-formula-isolated",
            1,
            compound,
            "NCPharmaDeliveryAutoinjector",
            MathF.Round(drug.TherapeuticConcentration * 0.8f, 1),
            [],
            "NCPharmaProductionStandard",
            0.7f,
            NCPharmaRegulatoryStatus.Experimental,
            null,
            codename,
            author,
            null);
        _derived.Add(id, formula);
        AddJournal(NCPharmaJournalKind.VersionCreated, id, string.Empty, author, "compound isolated from raw material");
        return true;
    }

    private void CaptureUnknown(NCRoundSnapshotCaptureEvent args)
    {
        var records = _unknownAssignments
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new NCPharmaUnknownSnapshot { Key = entry.Key, Compound = entry.Value.Pool })
            .ToList();
        if (_worldSeed != 0)
            records.Add(new NCPharmaUnknownSnapshot { Key = SeedKey, Compound = _worldSeed.ToString() });
        AddSimpleSection(args, UnknownSectionType, records.ToArray());
    }

    /// <summary>
    /// Brings the generated compounds back by regenerating them from the saved seed. It runs before anything that
    /// refers to a compound by id (bodies, formulas, knowledge), so none of them finds a missing prototype.
    /// </summary>
    private void OnSessionStartedEarly(NCActiveWorldSessionStartedEvent args)
    {
        _unknownAssignments.Clear();
        _worldSeed = 0;
        if (_restore.SelectionFailed || _restore.Manifest?.SnapshotId is not { } snapshotId)
            return;

        try
        {
            var records = ReadSimpleSection<NCPharmaUnknownSnapshot>(snapshotId, UnknownSectionType);
            foreach (var record in records)
            {
                if (record.Key == SeedKey && uint.TryParse(record.Compound, out var seed))
                    _worldSeed = seed;
            }

            if (_worldSeed == 0)
                return;

            foreach (var record in records.Where(record => record.Key != SeedKey))
            {
                if (LoadGenerated(record.Key, record.Compound, out var compound))
                    _unknownAssignments[record.Key] = (compound, record.Compound);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Generated compounds of snapshot {snapshotId} could not be restored: {exception}");
        }
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

public sealed class NCPharmaUnknownSnapshot
{
    public string Key { get; set; } = string.Empty;
    public string Compound { get; set; } = string.Empty;
}

public sealed class NCPharmaKnowledgeSnapshot
{
    public string Compound { get; set; } = string.Empty;
    public byte Tier { get; set; }
}
