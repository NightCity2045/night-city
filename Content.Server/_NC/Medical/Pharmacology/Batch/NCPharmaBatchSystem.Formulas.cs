// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>One line of the laboratory notebook of a formula lineage.</summary>
public sealed record NCPharmaJournalEntry(
    NCActiveWorldTime Time,
    NCPharmaJournalKind Kind,
    string FormulaId,
    string BatchCode,
    string Actor,
    string Detail);

/// <summary>
/// Formula development. A version is never edited: every change creates a new object with a new id and the
/// old one stays valid, so batches already produced keep pointing at exactly what they were made from.
/// </summary>
public sealed partial class NCPharmaBatchSystem
{
    private readonly Dictionary<string, NCPharmaFormulaPrototype> _derived = new();
    private readonly List<NCPharmaJournalEntry> _journal = [];

    public IReadOnlyList<NCPharmaJournalEntry> Journal => _journal;

    /// <summary>Every formula that exists: those authored as data and the versions developed in game.</summary>
    public IEnumerable<NCPharmaFormulaPrototype> AllFormulas =>
        _prototypes.EnumeratePrototypes<NCPharmaFormulaPrototype>().Concat(_derived.Values);

    public bool TryGetFormula(string id, [NotNullWhen(true)] out NCPharmaFormulaPrototype? formula)
    {
        if (_derived.TryGetValue(id, out formula))
            return true;

        return _prototypes.TryIndex<NCPharmaFormulaPrototype>(id, out formula);
    }

    /// <summary>
    /// The compounds a formula can be built on: those some formula already delivers. A compound that has not been
    /// identified yet is not offered, and neither is one that cannot be made from any raw material.
    /// </summary>
    public List<ProtoId<NCDrugProfilePrototype>> AvailableCores()
    {
        var cores = new List<ProtoId<NCDrugProfilePrototype>>();
        foreach (var formula in AllFormulas.OrderBy(formula => formula.ID, StringComparer.Ordinal))
        {
            foreach (var core in Cores(formula))
            {
                if (!cores.Contains(core) &&
                    _prototypes.TryIndex(core, out var compound) &&
                    compound.Feedstock != null &&
                    IsIdentified(core))
                {
                    cores.Add(core);
                }
            }
        }

        return cores;
    }

    /// <summary>The compounds a formula delivers, the main one first.</summary>
    public static IEnumerable<ProtoId<NCDrugProfilePrototype>> Cores(NCPharmaFormulaPrototype formula)
    {
        yield return formula.Compound;
        if (formula.SecondCompound is { } second)
            yield return second;
    }

    /// <summary>
    /// Builds a new experimental formula from the tree. A result with the same cores as the parent continues the
    /// parent's line as its next version; any other combination starts a line of its own. The name is derived
    /// from the cores and the version, so nobody can give a drug a misleading title.
    /// </summary>
    public bool TryComposeFormula(
        string? parentId,
        string author,
        string firstCoreId,
        string? secondCoreId,
        string deliveryFormId,
        float concentration,
        IReadOnlyList<string> modifierIds,
        string profileId,
        [NotNullWhen(true)] out NCPharmaFormulaPrototype? composed,
        out NCPharmaRdNotice notice)
    {
        composed = null;
        notice = NCPharmaRdNotice.InvalidRequest;
        if (!TryGetTuning(out var tuning) ||
            !_prototypes.TryIndex<NCDrugProfilePrototype>(firstCoreId, out var first) ||
            !_prototypes.TryIndex<NCPharmaDeliveryFormPrototype>(deliveryFormId, out var form) ||
            !_prototypes.HasIndex<NCPharmaProductionProfilePrototype>(profileId) ||
            !float.IsFinite(concentration) ||
            modifierIds.Count > NCPharmaBatchMath.MaximumModifiers ||
            modifierIds.Distinct().Count() != modifierIds.Count)
        {
            return false;
        }

        NCDrugProfilePrototype? second = null;
        if (secondCoreId != null && !_prototypes.TryIndex(secondCoreId, out second))
            return false;
        if (second != null && second.ID == first.ID)
        {
            notice = NCPharmaRdNotice.SameCore;
            return false;
        }

        var available = AvailableCores();
        if (!IsIdentified(first.ID) || (second != null && !IsIdentified(second.ID)))
        {
            notice = NCPharmaRdNotice.Unidentified;
            return false;
        }

        if (!available.Contains(first.ID) || (second != null && !available.Contains(second.ID)))
            return false;

        NCPharmaFormulaPrototype? parent = null;
        if (parentId != null && !TryGetFormula(parentId, out parent))
            return false;

        var continuesLine = parent != null &&
                            parent.Compound.Id == first.ID &&
                            parent.SecondCompound?.Id == second?.ID;
        var reference = continuesLine ? parent!.TargetConcentration : first.TherapeuticConcentration;
        var share = second != null ? NCPharmaBatchMath.SecondCoreShare : 1f;
        if (concentration < reference * tuning.MinimumConcentrationRatio ||
            concentration > reference * tuning.MaximumConcentrationRatio ||
            concentration * form.ConcentrationMultiplier * share > first.MaximumConcentration ||
            (second != null && concentration * form.ConcentrationMultiplier * share > second.MaximumConcentration))
        {
            return false;
        }

        var modifiers = new List<NCPharmaModifierPrototype>(modifierIds.Count);
        foreach (var modifierId in modifierIds)
        {
            if (!_prototypes.TryIndex<NCPharmaModifierPrototype>(modifierId, out var modifier))
                return false;
            modifiers.Add(modifier);
        }

        if (_derived.Count >= tuning.MaximumDerivedFormulas ||
            (!continuesLine && CountLineages() >= tuning.MaximumLineages))
        {
            notice = NCPharmaRdNotice.TooManyFormulas;
            return false;
        }

        // A local application only helps where a core has a local action; otherwise it would be wasted.
        if (NCPharmaBatchMath.ResolveLocalFraction(tuning, form, modifiers) > 0f &&
            !NCPharmaBatchMath.HasLocalAction(first) &&
            (second == null || !NCPharmaBatchMath.HasLocalAction(second)))
        {
            return false;
        }

        string root;
        int version;
        if (continuesLine)
        {
            root = ResolveRoot(parent!);
            version = AllFormulas.Where(candidate => ResolveRoot(candidate) == root).Max(candidate => candidate.Version) + 1;
        }
        else
        {
            root = NextLineageId();
            version = 1;
        }

        var id = $"{root}.v{version}";
        while (_derived.ContainsKey(id))
            id = $"{root}.v{++version}";

        var name = continuesLine ? parent!.Name : new LocId(NCLabText.DrugKey(first.ID));
        var stability = continuesLine ? parent!.BaseStability : ReferenceStability(first, second);
        var reaction = continuesLine ? parent!.Reaction : ReferenceReaction(first);
        composed = NCPharmaFormulaPrototype.Create(
            id,
            name,
            version,
            first.ID,
            deliveryFormId,
            concentration,
            [.. modifierIds.Select(value => new ProtoId<NCPharmaModifierPrototype>(value))],
            profileId,
            stability,
            NCPharmaRegulatoryStatus.Experimental,
            continuesLine ? parent!.ID : null,
            null,
            author,
            reaction,
            second == null ? null : second.ID);
        _derived.Add(id, composed);
        AddJournal(NCPharmaJournalKind.VersionCreated, id, string.Empty, author,
            $"{(continuesLine ? "continues " + parent!.ID : "new line")}: {first.ID}" +
            (second == null ? string.Empty : $" + {second.ID}") +
            $", {concentration:0.##} per dose, {modifierIds.Count} modifiers");
        notice = NCPharmaRdNotice.Created;
        return true;
    }

    private int CountLineages() => _derived.Values.Select(ResolveRoot).Distinct().Count();

    private string NextLineageId()
    {
        var number = 1;
        foreach (var id in _derived.Keys)
        {
            if (!id.StartsWith("NCRd", StringComparison.Ordinal))
                continue;
            var end = id.IndexOf('.', StringComparison.Ordinal);
            if (int.TryParse(end < 0 ? id[4..] : id[4..end], out var existing) && existing >= number)
                number = existing + 1;
        }

        return "NCRd" + number;
    }

    /// <summary>Stability a new line starts with: the weakest of the cores' own formulas, or a default.</summary>
    private float ReferenceStability(NCDrugProfilePrototype first, NCDrugProfilePrototype? second)
    {
        var stability = 1f;
        foreach (var core in new[] { first, second })
        {
            if (core == null)
                continue;
            var known = AllFormulas.FirstOrDefault(formula => formula.Compound.Id == core.ID);
            stability = MathF.Min(stability, known?.BaseStability ?? 0.85f);
        }

        return stability;
    }

    private ProtoId<NCPharmaReactionProfilePrototype>? ReferenceReaction(NCDrugProfilePrototype first) =>
        AllFormulas.FirstOrDefault(formula => formula.Compound.Id == first.ID)?.Reaction;

    /// <summary>
    /// What the laboratory knows about each node. Anything the stock of approved formulas uses is known; a node
    /// used only by a version developed in game is suspected until a batch of such a version was certified.
    /// </summary>
    public NCPharmaNodeKnowledge[] BuildNodeKnowledge()
    {
        var states = new Dictionary<string, NCPharmaNodeState>();

        void Raise(string id, NCPharmaNodeState state)
        {
            if (!states.TryGetValue(id, out var existing) || existing < state)
                states[id] = state;
        }

        foreach (var formula in AllFormulas)
        {
            var stock = !_derived.ContainsKey(formula.ID);
            var state = stock || HasCertifiedBatch(formula.ID)
                ? NCPharmaNodeState.Confirmed
                : NCPharmaNodeState.Suspected;
            foreach (var core in Cores(formula))
                Raise(NCPharmaNodeIds.Core(core.Id), stock || (GetKnowledge(core) & NCAnalysisTier.InVivo) != 0
                    ? NCPharmaNodeState.Confirmed
                    : state);
            Raise(NCPharmaNodeIds.Form(formula.DeliveryForm.Id), state);
            Raise(NCPharmaNodeIds.Profile(formula.DefaultProductionProfile.Id), state);
            foreach (var modifier in formula.Modifiers)
                Raise(NCPharmaNodeIds.Modifier(modifier.Id), state);
        }

        return states.Select(pair => new NCPharmaNodeKnowledge(pair.Key, pair.Value))
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The share of doses a raw material gives for a formula: the worst of its cores, 0 when one cannot use it.</summary>
    public float ResolveFeedstockYield(NCPharmaFormulaPrototype formula, string? feedstock)
    {
        var yield = 1f;
        foreach (var core in Cores(formula))
        {
            if (!_prototypes.TryIndex(core, out var compound))
                return 0f;
            yield = MathF.Min(yield, NCPharmaBatchMath.FeedstockYield(compound, feedstock));
        }

        return yield;
    }

    /// <summary>
    /// Writes a researcher's own note to the lab journal of a formula version. The system never writes the
    /// hypothesis for the researcher: this is where they say what they tried and what they concluded.
    /// </summary>
    public bool TryAddNote(string formulaId, string actor, string text, out NCPharmaRdNotice notice)
    {
        notice = NCPharmaRdNotice.InvalidNote;
        if (!TryGetTuning(out var tuning) || !TryGetFormula(formulaId, out _))
            return false;

        var clean = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0 || clean.Length > tuning.MaximumNoteLength)
            return false;

        AddJournal(NCPharmaJournalKind.Note, formulaId, string.Empty, actor, clean);
        notice = NCPharmaRdNotice.NoteSaved;
        return true;
    }

    /// <summary>
    /// Moves a developed version to regular production. It needs real evidence: at least one batch of this
    /// exact version that a laboratory run certified.
    /// </summary>
    public bool TryApproveFormula(string formulaId, string actor, out NCPharmaRdNotice notice)
    {
        notice = NCPharmaRdNotice.NotExperimental;
        if (!_derived.TryGetValue(formulaId, out var formula) ||
            formula.RegulatoryStatus != NCPharmaRegulatoryStatus.Experimental)
        {
            return false;
        }

        if (!HasCertifiedBatch(formulaId))
        {
            notice = NCPharmaRdNotice.NotCertified;
            return false;
        }

        _derived[formulaId] = formula.WithStatus(NCPharmaRegulatoryStatus.Prescription);
        AddJournal(NCPharmaJournalKind.Approved, formulaId, string.Empty, actor, "approved for regular production");
        notice = NCPharmaRdNotice.Approved;
        return true;
    }

    public bool CanApprove(NCPharmaFormulaPrototype formula) =>
        formula.RegulatoryStatus == NCPharmaRegulatoryStatus.Experimental &&
        _derived.ContainsKey(formula.ID) &&
        HasCertifiedBatch(formula.ID);

    public NCPharmaFormulaSummary BuildSummary(NCPharmaFormulaPrototype formula) => new(
        formula.ID,
        formula.Version,
        formula.CustomName == null ? formula.Name.Id : null,
        formula.CustomName,
        formula.Compound.Id,
        formula.DeliveryForm.Id,
        formula.TargetConcentration,
        formula.Modifiers.Select(modifier => modifier.Id).ToArray(),
        formula.DefaultProductionProfile.Id,
        formula.RegulatoryStatus,
        formula.DerivedFrom,
        CanApprove(formula),
        _prototypes.TryIndex(formula.Compound, out var summaryCompound) ? summaryCompound.ToxicConcentration : 0f,
        FeedstockOptions(formula),
        formula.SecondCompound?.Id);

    /// <summary>The raw materials a formula can be made from, the best first, with their share of doses.</summary>
    public NCPharmaFeedstockOption[] FeedstockOptions(NCPharmaFormulaPrototype formula)
    {
        var candidates = new HashSet<string>();
        foreach (var core in Cores(formula))
        {
            if (!_prototypes.TryIndex(core, out var compound))
                continue;
            if (compound.Feedstock is { } main)
                candidates.Add(main.Id);
            foreach (var alternative in compound.AlternateFeedstocks.Keys)
                candidates.Add(alternative.Id);
        }

        return candidates
            .Select(id => new NCPharmaFeedstockOption(id, ResolveFeedstockYield(formula, id)))
            .Where(option => option.Yield > 0f)
            .OrderByDescending(option => option.Yield)
            .ThenBy(option => option.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public NCPharmaFormulaSummary[] BuildFormulaSummaries(bool producibleOnly)
    {
        return AllFormulas
            .Where(formula => !producibleOnly ||
                              formula.RegulatoryStatus is not
                                  (NCPharmaRegulatoryStatus.Banned or NCPharmaRegulatoryStatus.Recalled))
            .OrderBy(formula => formula.ID, StringComparer.Ordinal)
            .Select(BuildSummary)
            .ToArray();
    }

    public NCPharmaJournalSummary[] BuildJournalSummary(string formulaId, int limit)
    {
        var lineage = new HashSet<string>();
        if (TryGetFormula(formulaId, out var selected))
        {
            var root = ResolveRoot(selected);
            foreach (var formula in AllFormulas.Where(candidate => ResolveRoot(candidate) == root))
                lineage.Add(formula.ID);
        }

        return _journal
            .Where(entry => lineage.Contains(entry.FormulaId))
            .Select(entry => new NCPharmaJournalSummary(entry.Kind, entry.FormulaId, entry.BatchCode, entry.Actor, entry.Detail))
            .Concat(BuildExperimentRows(lineage)) // NC: the full experiment records, as rows of the journal
            .TakeLast(limit)
            .ToArray();
    }

    /// <summary>Versions are tracked; the ordinary stock of approved formulas would only flood the notebook.</summary>
    public bool IsTracked(NCPharmaFormulaPrototype formula) => formula.DerivedFrom != null;

    public void AddJournal(NCPharmaJournalKind kind, string formulaId, string batchCode, string actor, string detail)
    {
        if (!TryGetTuning(out var tuning))
            return;

        if (_journal.Count >= tuning.MaximumJournalEntries)
            _journal.RemoveAt(0);
        _journal.Add(new NCPharmaJournalEntry(Now(), kind, formulaId, batchCode, JournalActor(actor), detail));
    }

    private bool HasCertifiedBatch(string formulaId) =>
        _batches.Values.Any(batch => batch.Formula == formulaId && batch.QcState == NCPharmaQcState.Certified);

    /// <summary>The lineage a formula belongs to: its id with every version suffix stripped.</summary>
    private string ResolveRoot(NCPharmaFormulaPrototype formula)
    {
        var current = formula;
        var guard = 0;
        while (current.DerivedFrom is { } parentId && guard++ < 1024 && TryGetFormula(parentId, out var parent))
            current = parent;
        return current.ID;
    }

    /// <summary>Notebook entries name a person, never a raw entity: only the part before the entity id is kept.</summary>
    private static string JournalActor(string actor)
    {
        var cut = actor.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? actor[..cut] : actor;
    }
}
