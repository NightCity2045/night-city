// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Server.GameObjects;

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

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmaRdConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdComposeMessage>(OnCompose);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdApproveMessage>(OnApprove);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCPharmaRdConsoleComponent, NCPharmaRdNoteMessage>(OnNote);
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
                _batches.BuildNodeKnowledge()));
    }
}
