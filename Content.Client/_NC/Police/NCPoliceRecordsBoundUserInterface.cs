// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Police.UI;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Police;

public sealed partial class NCPoliceRecordsBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCPoliceRecordsWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = new NCPoliceRecordsWindow(_prototypes);
        _window.OnSearch += query => SendMessage(new NCPoliceRecordsSearchMessage(query));
        _window.OnSelected += characterId => SendMessage(new NCPoliceRecordsSelectMessage(characterId));
        _window.OnStatusChanged += (characterId, status, reason) =>
            SendMessage(new NCPoliceRecordsChangeStatusMessage(characterId, status, reason));
        _window.OnCreateCase += (title, summary) => SendMessage(new NCPoliceCreateCaseMessage(title, summary));
        _window.OnSelectCase += caseId => SendMessage(new NCPoliceSelectCaseMessage(caseId));
        _window.OnAddCaseSubject += (caseId, role) =>
            SendMessage(new NCPoliceAddCaseSubjectMessage(caseId, role));
        _window.OnAddCaseEntry += (caseId, text) => SendMessage(new NCPoliceAddCaseEntryMessage(caseId, text));
        _window.OnCaseStatusChanged += (caseId, status, reason) =>
            SendMessage(new NCPoliceChangeCaseStatusMessage(caseId, status, reason));
        _window.OnCreateWarrant += (type, reason, caseId) =>
            SendMessage(new NCPoliceCreateWarrantMessage(type, reason, caseId));
        _window.OnResolveWarrant += (warrantId, status, reason) =>
            SendMessage(new NCPoliceResolveWarrantMessage(warrantId, status, reason));
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCPoliceRecordsUpdateMessage update)
            _window?.UpdateData(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
