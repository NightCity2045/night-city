// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Pharmacology;

public sealed partial class NCPharmaRdBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCPharmaRdWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<NCPharmaRdWindow>();
        _window.SelectRequested += formula => SendMessage(new NCPharmaRdSelectMessage(formula));
        _window.DeriveRequested += (parent, name, form, concentration, modifiers) =>
            SendMessage(new NCPharmaRdDeriveMessage(parent, name, form, concentration, modifiers));
        _window.ApproveRequested += formula => SendMessage(new NCPharmaRdApproveMessage(formula));
        _window.NoteRequested += (formula, text) => SendMessage(new NCPharmaRdNoteMessage(formula, text));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is NCPharmaRdState rd)
            _window?.UpdateState(rd);
    }
}
