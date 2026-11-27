// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Pharmacology;

/// <summary>
/// The client side of the two raw-material canisters of a machine: the same plan the server makes, so the window can
/// tell before the request whether the inserted material makes the selected formula and how many doses it gives.
/// </summary>
public static class NCPharmaSourceView
{
    /// <summary>The cores of a formula, as compound prototypes; empty when the client does not know one of them.</summary>
    public static List<NCDrugProfilePrototype> Cores(IPrototypeManager prototypes, NCPharmaFormulaSummary formula)
    {
        var cores = new List<NCDrugProfilePrototype>();
        foreach (var id in formula.SecondCompound is { } second ? new[] { formula.Compound, second } : new[] { formula.Compound })
        {
            if (!prototypes.TryIndex<NCDrugProfilePrototype>(id, out var core))
                return [];
            cores.Add(core);
        }

        return cores;
    }

    /// <summary>
    /// Plans a run of the given units as the server does: a canister without enough material is passed over while the
    /// other can serve. An unknown formula plans as made, the server has the last word.
    /// </summary>
    public static NCPharmaSourcePlan Plan(
        IPrototypeManager prototypes,
        NCPharmaFormulaSummary formula,
        NCPharmaSourceSummary? first,
        NCPharmaSourceSummary? second,
        int units)
    {
        var cores = Cores(prototypes, formula);
        if (cores.Count == 0)
            return new NCPharmaSourcePlan(first != null || second != null ? 1f : 0f, first != null, false);

        return NCPharmaBatchMath.PlanSources(
            cores,
            first?.Feedstock,
            first is { } a && a.Units >= units,
            second?.Feedstock,
            second is { } b && b.Units >= units);
    }

    /// <summary>The raw materials one compound is made from, the main one first, with their share of doses.</summary>
    public static NCPharmaFeedstockOption[] Options(NCDrugProfilePrototype core)
    {
        if (core.Feedstock is not { } main)
            return [];

        var options = new List<NCPharmaFeedstockOption> { new(main.Id, 1f) };
        foreach (var (id, share) in core.AlternateFeedstocks)
            options.Add(new NCPharmaFeedstockOption(id.Id, share));
        options.Sort((x, y) => y.Yield.CompareTo(x.Yield));
        return options.ToArray();
    }

    /// <summary>The window line of one canister.</summary>
    public static string Describe(IPrototypeManager prototypes, NCPharmaSourceSummary? source, bool second)
    {
        if (source is not { } canister)
            return Loc.GetString(second ? "nc-pharma-station-second-source-missing" : "nc-pharma-station-source-missing");

        return Loc.GetString(second ? "nc-pharma-station-second-source-present" : "nc-pharma-station-source-present",
            ("units", canister.Units.ToString("0.#")),
            ("purity", canister.Purity.ToString("P0")),
            ("type", FeedstockName(prototypes, canister.Feedstock)));
    }

    public static string FeedstockName(IPrototypeManager prototypes, string? id) =>
        id != null && prototypes.TryIndex<NCPharmaFeedstockPrototype>(id, out var type)
            ? Loc.GetString(type.Name)
            : Loc.GetString("nc-pharma-feedstock-none");
}
