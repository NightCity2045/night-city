// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Research console logic. Every request is a question to the formula registry; the console never edits a
/// formula, it only asks for a new version or for an approval and shows the answer.
/// </summary>
public sealed partial class NCPharmaRdSystem : EntitySystem
{
    private const int JournalLimit = 24;

    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaRdConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdComposeMessage>(OnCompose);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdApproveMessage>(OnApprove);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdNoteMessage>(OnNote);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdInventMessage>(OnInvent);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, EntInsertedIntoContainerMessage>(OnSlotChanged);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, EntRemovedFromContainerMessage>(OnSlotChanged);
    }

    private void OnUiOpened(Entity<NCPharmaRdConsoleComponent> console, ref BoundUIOpenedEvent args)
    {
        UpdateUi(console, NCPharmaRdNotice.None);
    }

    private void OnSelect(Entity<NCPharmaRdConsoleComponent> console, ref NCPharmaRdSelectMessage args)
    {
        console.Comp.Selected = _batches.TryGetFormula(args.Formula, out var formula) ? formula.ID : string.Empty;
        UpdateUi(console, NCPharmaRdNotice.None);
    }

    private void OnCompose(Entity<NCPharmaRdConsoleComponent> console, ref NCPharmaRdComposeMessage args)
    {
        var notice = NCPharmaRdNotice.InvalidRequest;
        if (args.Modifiers != null &&
            _batches.TryComposeFormula(
                args.Parent,
                Name(args.Actor),
                args.FirstCore,
                args.SecondCore,
                args.DeliveryForm,
                args.Concentration,
                args.Modifiers,
                args.Profile,
                out var composed,
                out notice))
        {
            console.Comp.Selected = composed.ID;
        }

        UpdateUi(console, notice);
    }

    private void OnSlotChanged(Entity<NCPharmaRdConsoleComponent> console, ref EntInsertedIntoContainerMessage args)
    {
        UpdateUi(console, NCPharmaRdNotice.None);
    }

    private void OnSlotChanged(Entity<NCPharmaRdConsoleComponent> console, ref EntRemovedFromContainerMessage args)
    {
        UpdateUi(console, NCPharmaRdNotice.None);
    }

    /// <summary>What the console's beaker holds, if there is one.</summary>
    public bool TryGetBeaker(
        Entity<NCPharmaRdConsoleComponent> console,
        out Entity<Content.Shared.Chemistry.Components.SolutionComponent> solution)
    {
        solution = default;
        if (!_slots.TryGetSlot(console, console.Comp.ReagentSlot, out var slot) ||
            slot.Item is not { } beaker ||
            !_solutions.TryGetDrainableSolution(beaker, out var found, out _))
        {
            return false;
        }

        solution = found.Value;
        return true;
    }

    /// <summary>
    /// Invents a compound from the chosen ingredients. The beaker must hold enough of every one; the ingredients are
    /// spent only when the compound is made.
    /// </summary>
    public NCPharmaRdNotice TryInvent(Entity<NCPharmaRdConsoleComponent> console, EntityUid user, string[] ingredients)
    {
        if (ingredients == null || !TryGetBeaker(console, out var beaker))
            return NCPharmaRdNotice.NoReagents;

        foreach (var reagent in ingredients.Distinct())
        {
            if ((float) beaker.Comp.Solution.GetTotalPrototypeQuantity(reagent) < console.Comp.UnitsPerIngredient)
                return NCPharmaRdNotice.NoReagents;
        }

        if (!_batches.TryInventCompound(Name(user), ingredients, out var formula, out var notice))
            return notice;

        foreach (var reagent in ingredients)
            _solutions.RemoveReagent(beaker, reagent, Content.Shared.FixedPoint.FixedPoint2.New(console.Comp.UnitsPerIngredient));
        console.Comp.Selected = formula.ID;
        return notice;
    }

    private void OnInvent(Entity<NCPharmaRdConsoleComponent> console, ref NCPharmaRdInventMessage args)
    {
        UpdateUi(console, TryInvent(console, args.Actor, args.Ingredients));
    }

    private void OnNote(Entity<NCPharmaRdConsoleComponent> console, ref NCPharmaRdNoteMessage args)
    {
        _batches.TryAddNote(args.Formula, Name(args.Actor), args.Text, out var notice);
        UpdateUi(console, notice);
    }

    private void OnApprove(Entity<NCPharmaRdConsoleComponent> console, ref NCPharmaRdApproveMessage args)
    {
        _batches.TryApproveFormula(args.Formula, Name(args.Actor), out var notice);
        UpdateUi(console, notice);
    }

    private void UpdateUi(Entity<NCPharmaRdConsoleComponent> console, NCPharmaRdNotice notice)
    {
        _ui.SetUiState(
            console.Owner,
            NCPharmaRdUiKey.Key,
            new NCPharmaRdState(
                _batches.BuildFormulaSummaries(false),
                _batches.BuildJournalSummary(console.Comp.Selected, JournalLimit),
                notice,
                _batches.AvailableCores().Select(core => core.Id).ToArray(),
                _batches.BuildNodeKnowledge(),
                TryGetBeaker(console, out var beaker),
                BeakerContents(beaker)));
    }

    private NCPharmaReagentAmount[] BeakerContents(Entity<Content.Shared.Chemistry.Components.SolutionComponent> beaker)
    {
        if (beaker.Comp == null)
            return [];

        return beaker.Comp.Solution.Contents
            .Select(reagent => new NCPharmaReagentAmount(reagent.Reagent.Prototype, (float) reagent.Quantity))
            .ToArray();
    }
}
