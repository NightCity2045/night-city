// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Research console logic. Every request is a question to the formula registry; the console never edits a
/// formula, it only asks for a new version or for an approval and shows the answer.
/// </summary>
public sealed partial class NCPharmaRdSystem : EntitySystem
{
    private const int JournalLimit = 24;

    /// <summary>How far from the machine where the work was done a console still belongs to that laboratory.</summary>
    private const float LaboratoryRange = 15f;

    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCPharmaBatchSystem _batches = default!;
    [Dependency] private ItemSlotsSystem _slots = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaRdConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdComposeMessage>(OnCompose);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdApproveMessage>(OnApprove);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdNoteMessage>(OnNote);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdInventMessage>(OnInvent);
        SubscribeLocalEvent<NCPharmaResearchEvent>(OnResearch);
        SubscribeLocalEvent<NCPharmaResearchChipComponent, MapInitEvent>(OnChipMapInit);
        SubscribeLocalEvent<NCPharmaResearchChipComponent, ExaminedEvent>(OnChipExamined);
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
        if (!TryGetChip(console, out var chip))
        {
            UpdateUi(console, NCPharmaRdNotice.NoChip);
            return;
        }

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
                out notice,
                chip.Comp.Researched,
                console.Owner))
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

    /// <summary>The research chip in the console, if there is one.</summary>
    public bool TryGetChip(Entity<NCPharmaRdConsoleComponent> console, out Entity<NCPharmaResearchChipComponent> chip)
    {
        chip = default;
        if (!_slots.TryGetSlot(console, console.Comp.ChipSlot, out var slot) ||
            slot.Item is not { } item ||
            !TryComp(item, out NCPharmaResearchChipComponent? found))
        {
            return false;
        }

        chip = (item, found);
        return true;
    }

    /// <summary>A laboratory learnt about nodes: the chips in the consoles of that laboratory record them.</summary>
    private void OnResearch(ref NCPharmaResearchEvent args)
    {
        MapCoordinates? site = args.Site is { } origin && Exists(origin) ? _transform.GetMapCoordinates(origin) : null;
        if (args.Site != null && site == null)
            return;

        var query = EntityQueryEnumerator<NCPharmaRdConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var console = new Entity<NCPharmaRdConsoleComponent>(uid, comp);
            if (site is { } where && !_transform.GetMapCoordinates(uid).InRange(where, LaboratoryRange))
                continue;
            if (!TryGetChip(console, out var chip))
                continue;

            var changed = false;
            foreach (var node in args.Nodes)
                changed |= chip.Comp.Researched.Add(node);
            if (changed)
                UpdateUi(console, NCPharmaRdNotice.None);
        }
    }

    private void OnChipMapInit(Entity<NCPharmaResearchChipComponent> chip, ref MapInitEvent args)
    {
        if (!chip.Comp.Stock)
            return;

        foreach (var node in _batches.StockNodes())
            chip.Comp.Researched.Add(node);
    }

    private void OnChipExamined(Entity<NCPharmaResearchChipComponent> chip, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("nc-pharma-research-chip-examine", ("count", chip.Comp.Researched.Count)));
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
        if (!TryGetChip(console, out _))
            return NCPharmaRdNotice.NoChip;
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
        var hasChip = TryGetChip(console, out var chip);
        _ui.SetUiState(
            console.Owner,
            NCPharmaRdUiKey.Key,
            new NCPharmaRdState(
                _batches.BuildFormulaSummaries(false),
                _batches.BuildJournalSummary(console.Comp.Selected, JournalLimit),
                notice,
                ResearchedCores(chip),
                ResearchedKnowledge(chip),
                TryGetBeaker(console, out var beaker),
                BeakerContents(beaker),
                hasChip,
                hasChip ? chip.Comp.Researched.Count(NCPharmaNodeIds.IsCore) : 0));
    }

    private NCPharmaReagentAmount[] BeakerContents(Entity<Content.Shared.Chemistry.Components.SolutionComponent> beaker)
    {
        if (beaker.Comp == null)
            return [];

        return beaker.Comp.Solution.Contents
            .Select(reagent => new NCPharmaReagentAmount(reagent.Reagent.Prototype, (float) reagent.Quantity))
            .ToArray();
    }

    /// <summary>The compounds the chip's research lets a formula be built on.</summary>
    private string[] ResearchedCores(Entity<NCPharmaResearchChipComponent> chip)
    {
        if (chip.Comp == null)
            return [];

        return _batches.AvailableCores()
            .Select(core => core.Id)
            .Where(core => chip.Comp.Researched.Contains(NCPharmaNodeIds.Core(core)))
            .ToArray();
    }

    /// <summary>
    /// What is known about each node. A compound is known only once the chip has studied it; delivery forms, modifiers and
    /// production modes are open to every laboratory, so the chip does not limit them.
    /// </summary>
    private NCPharmaNodeKnowledge[] ResearchedKnowledge(Entity<NCPharmaResearchChipComponent> chip)
    {
        if (chip.Comp == null)
            return [];

        return _batches.BuildNodeKnowledge()
            .Where(node => !NCPharmaNodeIds.IsCore(node.Id) || chip.Comp.Researched.Contains(node.Id))
            .ToArray();
    }
}
