// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#pragma warning disable RA0039 // Invented compounds are prototypes made at runtime, like the generated ones.

using System.Diagnostics.CodeAnalysis;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

public sealed partial class NCPharmaBatchSystem
{
    /// <summary>The pool name that marks an invented compound in the saved list of generated ones.</summary>
    public const string InventedPool = "invented";

    /// <summary>
    /// Reads the ingredient a reagent stands for, or nothing when the reagent is not one a laboratory can build
    /// a compound from.
    /// </summary>
    public bool TryGetIngredient(string reagent, [NotNullWhen(true)] out NCPharmaIngredientPrototype? ingredient)
    {
        foreach (var candidate in _prototypes.EnumeratePrototypes<NCPharmaIngredientPrototype>())
        {
            if (candidate.Reagent.Id != reagent)
                continue;
            ingredient = candidate;
            return true;
        }

        ingredient = null;
        return false;
    }

    /// <summary>
    /// Invents a compound out of two or three ingredient reagents and gives the laboratory an experimental formula
    /// for it. The compound is a real drug profile, so all of pharmacology treats it like any other. It stays
    /// unidentified until it is studied, so what it really does is learnt by making and testing a batch.
    /// </summary>
    public bool TryInventCompound(
        string author,
        IReadOnlyList<string> reagents,
        [NotNullWhen(true)] out NCPharmaFormulaPrototype? formula,
        out NCPharmaRdNotice notice)
    {
        formula = null;
        notice = NCPharmaRdNotice.InventionInvalid;
        if (reagents.Count is < NCPharmaInvention.MinimumIngredients or > NCPharmaInvention.MaximumIngredients ||
            reagents.Distinct().Count() != reagents.Count)
        {
            return false;
        }

        var traits = new List<NCIngredientTraits>();
        foreach (var reagent in reagents)
        {
            if (!TryGetIngredient(reagent, out var ingredient))
                return false;
            traits.Add(new NCIngredientTraits(
                reagent, ingredient.Category, ingredient.Potency, ingredient.Toxicity, ingredient.Side, ingredient.Dependence));
        }

        if (!NCPharmaInvention.TryCombine(traits, out _))
            return false;

        var key = NCPharmaInvention.KeyOf(reagents);
        if (!_unknownAssignments.TryGetValue(key, out var existing))
        {
            if (!LoadInvented(key, out var created))
                return false;
            existing = (created, InventedPool);
            _unknownAssignments[key] = existing;
        }

        if (!_prototypes.TryIndex(existing.Compound, out var drug) ||
            !TryRegisterCompoundFormula(existing.Compound, drug, author, "compound invented from ingredients",
                out var registered, out _))
        {
            notice = NCPharmaRdNotice.TooManyFormulas;
            return false;
        }

        formula = registered;
        notice = NCPharmaRdNotice.Invented;
        return true;
    }

    /// <summary>Loads the drug profile of an invented compound. The key says which ingredients made it.</summary>
    private bool LoadInvented(string key, out ProtoId<NCDrugProfilePrototype> compound)
    {
        compound = NCPharmaInvention.IdFor(key);
        if (_prototypes.HasIndex<NCDrugProfilePrototype>(compound))
            return true;

        var traits = new List<NCIngredientTraits>();
        foreach (var reagent in key.Split('+'))
        {
            if (!TryGetIngredient(reagent, out var ingredient))
                return false;
            traits.Add(new NCIngredientTraits(
                reagent, ingredient.Category, ingredient.Potency, ingredient.Toxicity, ingredient.Side, ingredient.Dependence));
        }

        if (!NCPharmaInvention.TryCombine(traits, out var profile))
            return false;

        try
        {
            _prototypes.LoadString(NCPharmaInvention.ToYaml(key, profile));
            _prototypes.ResolveResults();
        }
        catch (Exception exception)
        {
            Log.Error($"Invented compound for {key} could not be loaded: {exception}");
            return false;
        }

        if (_prototypes.TryIndex(compound, out var drug) && ValidateGenerated(drug))
            return true;

        Log.Error($"Invented compound {compound} for {key} is not a valid drug profile.");
        return false;
    }
}
