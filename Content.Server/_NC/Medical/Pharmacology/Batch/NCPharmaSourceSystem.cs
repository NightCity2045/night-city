// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>What a source canister holds, read from its solution.</summary>
public readonly record struct NCPharmaSourceReading(
    float Units,
    float Purity,
    float Contaminants,
    string Feedstock,
    Entity<SolutionComponent> Solution);

/// <summary>
/// Source material is a liquid in a canister. This system is the one place that turns the canister's composition
/// into the numbers a synthesis run needs, and the numbers back into a composition.
/// </summary>
public sealed partial class NCPharmaSourceSystem : EntitySystem
{
    /// <summary>A neutral filler. It dilutes the wanted material and does nothing else.</summary>
    public static readonly ProtoId<ReagentPrototype> ImpurityReagent = "NCPharmaImpurity";

    /// <summary>The toxic by-product. Its share of the canister becomes hidden contamination of every batch.</summary>
    public static readonly ProtoId<ReagentPrototype> SludgeReagent = "NCPharmaSludge";

    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPharmaSourceComponent, ExaminedEvent>(OnExamined);
    }

    private void OnExamined(Entity<NCPharmaSourceComponent> canister, ref ExaminedEvent args)
    {
        if (!TryRead(canister, out var reading))
        {
            args.PushMarkup(Loc.GetString("nc-pharma-source-empty"));
            return;
        }

        if (_prototypes.TryIndex<NCPharmaFeedstockPrototype>(reading.Feedstock, out var type))
            args.PushMarkup(Loc.GetString("nc-pharma-feedstock-examine", ("type", Loc.GetString(type.Name))));
        args.PushMarkup(Loc.GetString("nc-pharma-source-examine",
            ("units", reading.Units.ToString("0.#")), ("purity", reading.Purity.ToString("P0"))));
    }

    /// <summary>
    /// Reads a canister. The feedstock is the kind there is most of; the other kinds are as good as impurity.
    /// Purity is that kind's share of the whole solution and contamination is the share of sludge.
    /// </summary>
    public bool TryRead(EntityUid canister, out NCPharmaSourceReading reading)
    {
        reading = default;
        if (!_solutions.TryGetDrainableSolution(canister, out var soln, out var solution))
            return false;

        var total = (float) solution.Volume;
        if (total <= 0f)
            return false;

        NCPharmaFeedstockPrototype? best = null;
        var amount = 0f;
        foreach (var candidate in _prototypes.EnumeratePrototypes<NCPharmaFeedstockPrototype>())
        {
            var held = (float) solution.GetTotalPrototypeQuantity(candidate.Source);
            if (held <= amount)
                continue;
            best = candidate;
            amount = held;
        }

        if (best == null || amount <= 0f)
            return false;

        var sludge = (float) solution.GetTotalPrototypeQuantity(SludgeReagent);
        reading = new NCPharmaSourceReading(
            amount,
            Math.Clamp(amount / total, 0f, 1f),
            Math.Clamp(sludge / total, 0f, 1f),
            best.ID,
            soln.Value);
        return true;
    }

    /// <summary>
    /// Spends units of the wanted material. Everything else leaves in the same proportion, so what is left keeps
    /// its purity and contamination.
    /// </summary>
    public void Consume(NCPharmaSourceReading reading, float units)
    {
        if (units <= 0f || reading.Units <= 0f)
            return;

        var share = Math.Clamp(units / reading.Units, 0f, 1f);
        foreach (var held in reading.Solution.Comp.Solution.Contents.ToArray())
        {
            _solutions.RemoveReagent(reading.Solution, held.Reagent.Prototype, held.Quantity * share);
        }
    }

    /// <summary>Replaces the contents of a canister with a composition that reads back as the given numbers.</summary>
    public bool Fill(
        EntityUid canister,
        string feedstock,
        float units,
        float purity,
        float contaminants)
    {
        if (!_prototypes.TryIndex<NCPharmaFeedstockPrototype>(feedstock, out var type) ||
            !_solutions.TryGetDrainableSolution(canister, out var soln, out var solution))
        {
            return false;
        }

        units = Math.Max(units, 0f);
        purity = Math.Clamp(purity, 0.01f, 1f);
        var total = Math.Min(units / purity, (float) solution.MaxVolume);
        var wanted = Math.Min(units, total * purity);
        var sludge = Math.Clamp(contaminants * total, 0f, total - wanted);
        var impurity = Math.Max(total - wanted - sludge, 0f);

        _solutions.RemoveAllSolution(soln.Value);
        _solutions.TryAddReagent(soln.Value, type.Source, FixedPoint2.New(wanted));
        if (sludge > 0f)
            _solutions.TryAddReagent(soln.Value, SludgeReagent, FixedPoint2.New(sludge));
        if (impurity > 0f)
            _solutions.TryAddReagent(soln.Value, ImpurityReagent, FixedPoint2.New(impurity));
        return true;
    }
}
