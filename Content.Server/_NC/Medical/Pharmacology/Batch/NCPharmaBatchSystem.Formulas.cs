// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Persistence.Time;

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
    /// Develops a new experimental version of a formula. Quantities are bounded against the version it comes
    /// from and against the compound's own limits, so a version can be bold but never impossible.
    /// </summary>
    public bool TryDeriveFormula(
        string parentId,
        string author,
        string name,
        string deliveryFormId,
        float concentration,
        IReadOnlyList<string> modifierIds,
        [NotNullWhen(true)] out NCPharmaFormulaPrototype? derived,
        out NCPharmaRdNotice notice)
    {
        derived = null;
        notice = NCPharmaRdNotice.InvalidRequest;
        if (TryGetFormula(parentId, out var guardParent) && !IsIdentified(guardParent.Compound))
        {
            notice = NCPharmaRdNotice.Unidentified; // NC
            return false;
        }

        if (!TryGetTuning(out var tuning) ||
            !TryGetFormula(parentId, out var parent) ||
            !_prototypes.TryIndex<NCPharmaDeliveryFormPrototype>(deliveryFormId, out var form) ||
            !_prototypes.TryIndex(parent.Compound, out var compound) ||
            !float.IsFinite(concentration) ||
            modifierIds.Count > NCPharmaBatchMath.MaximumModifiers ||
            modifierIds.Distinct().Count() != modifierIds.Count ||
            concentration < parent.TargetConcentration * tuning.MinimumConcentrationRatio ||
            concentration > parent.TargetConcentration * tuning.MaximumConcentrationRatio ||
            concentration * form.ConcentrationMultiplier > compound.MaximumConcentration)
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

        if (_derived.Count >= tuning.MaximumDerivedFormulas)
        {
            notice = NCPharmaRdNotice.TooManyFormulas;
            return false;
        }

        var cleanName = SanitizeFormulaName(name, tuning.MaximumFormulaNameLength);
        if (cleanName == null)
        {
            notice = NCPharmaRdNotice.InvalidName;
            return false;
        }

        var root = ResolveRoot(parent);
        var version = AllFormulas.Where(candidate => ResolveRoot(candidate) == root).Max(candidate => candidate.Version) + 1;
        var id = $"{root}.v{version}";
        while (_derived.ContainsKey(id))
            id = $"{root}.v{++version}";

        derived = NCPharmaFormulaPrototype.Create(
            id,
            parent.Name,
            version,
            parent.Compound,
            deliveryFormId,
            concentration,
            [.. modifierIds.Select(value => new Robust.Shared.Prototypes.ProtoId<NCPharmaModifierPrototype>(value))],
            parent.DefaultProductionProfile,
            parent.BaseStability,
            NCPharmaRegulatoryStatus.Experimental,
            parent.ID,
            cleanName,
            author,
            parent.Reaction);
        _derived.Add(id, derived);
        AddJournal(NCPharmaJournalKind.VersionCreated, id, string.Empty, author,
            $"derived from {parent.ID}: {concentration:0.##} per dose, {modifierIds.Count} modifiers");
        notice = NCPharmaRdNotice.Created;
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
        CanApprove(formula));

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
            .TakeLast(limit)
            .Select(entry => new NCPharmaJournalSummary(entry.Kind, entry.FormulaId, entry.BatchCode, entry.Actor, entry.Detail))
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

    private static string? SanitizeFormulaName(string name, int maximumLength)
    {
        var trimmed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (trimmed.Length < 1 || trimmed.Length > maximumLength)
            return null;

        return trimmed.All(value => char.IsLetterOrDigit(value) || value is ' ' or '-' or '_' or '.' or ':')
            ? trimmed
            : null;
    }
}
