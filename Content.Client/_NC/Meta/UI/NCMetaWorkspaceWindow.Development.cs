// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private NCMetaDevelopmentState? _developmentView;
    private Guid _developmentSelection;
    private Guid _developmentPending;
    private float _developmentWait;
    private string _developmentDeveloper = "";
    private readonly List<NCMetaDevelopmentAction> _developmentActions = [];
    private readonly List<NCMetaDeviceSummary> _developmentDevices = [];
    private NCMetaDevelopmentConfirmationWindow? _developmentDialog;
    private byte[]? _developmentFixtureBytes;
    private int _developmentFixtureReceived;
    private string _developmentFixturePath = "";
    private string _developmentFixtureHash = "";
    private Guid _developmentFixtureContract;
    private long _developmentFixtureRevision;
    private Guid _developmentSearchRequest;
    private readonly List<NCMetaSoftwareContactSummary> _developmentContacts = [];
    private readonly List<NCMetaFileSummary> _developmentFixtures = [];

    private void InitializeDevelopment()
    {
        TabContainer.SetTabTitle(DevelopmentTab, Loc.GetString("nc-meta-development-contracts"));
        TabContainer.SetTabTitle(DevelopmentReview, Loc.GetString("nc-meta-development-review"));
        TabContainer.SetTabTitle(DevelopmentOfferPanel, Loc.GetString("nc-meta-development-create-offer"));
        DevelopmentArbitrator.AddItem(Loc.GetString("nc-meta-development-no-arbitrator"), 0);
        DevelopmentArbitrator.SelectId(0);
        DevelopmentArbitrator.OnItemSelected += args => DevelopmentArbitrator.SelectId(args.Id);
        MainContentTabs.OnTabChanged += _ => { if (DevelopmentTab.VisibleInTree && _developmentView == null) ReadDevelopment(); };
        DevelopmentRefresh.OnPressed += _ => ReadDevelopment();
        DevelopmentPrevious.OnPressed += _ => ReadDevelopment(Math.Max(0, (_developmentView?.Offset ?? 0) - 8));
        DevelopmentNext.OnPressed += _ => { if (_developmentView?.HasMore == true) ReadDevelopment(_developmentView.Offset + 8); };
        DevelopmentRows.OnItemSelected += args =>
        {
            if (_applying || _developmentPending != Guid.Empty || _developmentView == null || args.ItemIndex < 0 || args.ItemIndex >= _developmentView.Rows.Count) return;
            _developmentSelection = _developmentView.Rows[args.ItemIndex].Id;
            ResetDevelopmentConsent();
            ReadDevelopment(_developmentView.Offset);
        };
        DevelopmentContacts.OnItemSelected += args =>
        {
            if (_applying || _developmentView == null || args.ItemIndex < 0 || args.ItemIndex >= _developmentView.Contacts.Count) return;
            _developmentDeveloper = _developmentView.Contacts[args.ItemIndex].Party;
        };
        DevelopmentContactSearch.OnPressed += _ => SendDevelopment(new(NCMetaDevelopmentAction.SearchContacts, "",
            ContractId: _developmentSelection, Text: DevelopmentContactQuery.Text, Offset: _developmentView?.Offset ?? 0));
        DevelopmentOffer.OnPressed += _ => OfferDevelopment();
        DevelopmentAction.OnItemSelected += args => { DevelopmentAction.SelectId(args.Id); UpdateDevelopmentButtons(); };
        DevelopmentConsent.OnToggled += _ => UpdateDevelopmentButtons();
        DevelopmentAutoConsent.OnToggled += _ => UpdateDevelopmentButtons();
        DevelopmentExecute.OnPressed += _ => ExecuteDevelopment();
        DevelopmentInbox.OnItemSelected += args => DevelopmentImport.Disabled = _developmentView == null ||
            args.ItemIndex < 0 || args.ItemIndex >= _developmentView.Inbox.Count || !_developmentView.Inbox[args.ItemIndex].CanImport;
        DevelopmentImport.OnPressed += _ =>
        {
            var item = DevelopmentInbox.GetSelected().Select(DevelopmentInbox.IndexOf).FirstOrDefault(-1);
            if (_developmentView != null && item >= 0 && item < _developmentView.Inbox.Count && _developmentView.Inbox[item].CanImport)
                SendDevelopment(new(NCMetaDevelopmentAction.ImportSource, "", ContractId: _developmentView.Inbox[item].ClaimId));
        };
        DevelopmentReadFixture.OnPressed += _ =>
        {
            if (_developmentView?.Selected is not { } selected) return;
            var index = DevelopmentFrozenFixtures.GetSelected().Select(DevelopmentFrozenFixtures.IndexOf).FirstOrDefault(-1);
            if (index < 0 || index >= selected.Fixtures.Count) return;
            _developmentFixturePath = selected.Fixtures[index];
            _developmentFixtureContract = selected.Id; _developmentFixtureRevision = selected.Revision;
            _developmentFixtureBytes = null;
            _developmentFixtureReceived = 0;
            SetDevelopmentText(DevelopmentFixtureText, "");
            SendDevelopment(DevelopmentCommand(NCMetaDevelopmentAction.ReadFixture) with { Text = _developmentFixturePath });
        };
        UpdateDevelopmentButtons();
    }
    private void ReadDevelopment(int? offset = null) => SendDevelopment(new(NCMetaDevelopmentAction.Read, "",
        ContractId: _developmentSelection, Offset: offset ?? _developmentView?.Offset ?? 0));
    private void SendDevelopment(NCMetaDevelopmentRequest request)
    {
        if (_developmentPending != Guid.Empty) return;
        _developmentPending = Guid.NewGuid(); _developmentWait = 0;
        if (request.Action == NCMetaDevelopmentAction.SearchContacts) _developmentSearchRequest = _developmentPending;
        Requested?.Invoke(new(NCMetaWorkspaceOperation.DevelopmentContract, _developmentPending, payload: request,
            deviceId: _selectedDeviceId, releaseId: _state.ActiveReleaseId, projectId: _state.ProjectId));
        UpdateDevelopmentButtons();
    }
    private NCMetaDevelopmentRequest DevelopmentCommand(NCMetaDevelopmentAction action)
    {
        var selected = _developmentView?.Selected;
        return new(action, "", selected?.Id ?? Guid.Empty, selected?.Revision ?? 0, selected?.TermsHash ?? "",
            selected?.DeliveryId ?? Guid.Empty, selected?.ReceiptId ?? Guid.Empty, Confirm: DevelopmentConsent.Pressed,
            AutomaticConsent: DevelopmentAutoConsent.Pressed, ProjectId: _state.ProjectId, ReleaseId: _state.ActiveReleaseId,
            Text: DevelopmentReason.Text, ResolutionHash: selected?.ResolutionHash ?? "", Offset: _developmentView?.Offset ?? 0);
    }
    private void ExecuteDevelopment()
    {
        if (DevelopmentExecute.Disabled || DevelopmentAction.SelectedId < 0 || DevelopmentAction.SelectedId >= _developmentActions.Count) return;
        var action = _developmentActions[DevelopmentAction.SelectedId];
        var request = DevelopmentCommand(action);
        if (action == NCMetaDevelopmentAction.ProposeResolution)
        {
            if (!long.TryParse(DevelopmentCustomerAmount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var customer) ||
                !long.TryParse(DevelopmentDeveloperAmount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var developer))
            { Status.Text = Loc.GetString("nc-meta-development-input"); return; }
            request = request with { CustomerAmount = customer, DeveloperAmount = developer, TransferDelivery = DevelopmentTransfer.Pressed };
        }
        SendDevelopment(request);
    }
    private void OfferDevelopment()
    {
        if (_developmentView == null || _developmentDeveloper.Length == 0 ||
            !long.TryParse(DevelopmentPrice.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var price) ||
            !int.TryParse(DevelopmentDeadline.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var due) ||
            !int.TryParse(DevelopmentSupport.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var support) ||
            !long.TryParse(DevelopmentResident.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var resident) ||
            !long.TryParse(DevelopmentMemory.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var memory) ||
            !long.TryParse(DevelopmentStorage.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var storage))
        { Status.Text = Loc.GetString("nc-meta-development-input"); return; }
        var devices = DevelopmentTargets.GetSelected().Select(DevelopmentTargets.IndexOf).Where(index => index >= 0 && index < _developmentDevices.Count)
            .Select(index => _developmentDevices[index].DeviceId).Take(4).ToList();
        var fixtures = DevelopmentOfferFixtures.GetSelected().Select(DevelopmentOfferFixtures.IndexOf)
            .Where(index => index >= 0 && index < _developmentView.Fixtures.Count).Select(index => _developmentView.Fixtures[index].FileId).Take(17).ToList();
        if (devices.Count is < 1 or > 3 || fixtures.Count is < 1 or > 16) { Status.Text = Loc.GetString("nc-meta-development-input"); return; }
        var arbitrator = DevelopmentArbitrator.SelectedId > 0 && DevelopmentArbitrator.SelectedId <= _developmentView.Contacts.Count
            ? _developmentView.Contacts[DevelopmentArbitrator.SelectedId - 1].Party : null;
        var offer = new NCMetaDevelopmentOffer(_developmentView.Owner, _developmentDeveloper, DevelopmentOfferSubject.Text,
            Rope.Collapse(DevelopmentRequirements.TextRope), DevelopmentFailure.Text, price, due, support,
            DevelopmentOfferAuto.Pressed, DevelopmentOfferSource.Pressed, devices, fixtures,
            DevelopmentScopes.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(65).ToList(), resident, memory, storage, arbitrator);
        SendDevelopment(new(NCMetaDevelopmentAction.Offer, "", ProjectId: _state.ProjectId, Offer: offer));
    }
    private void ApplyDevelopmentState(NCMetaDevelopmentState? state)
    {
        if (state == null) return;
        if (_developmentPending != Guid.Empty && _state.AcknowledgedRequestId == _developmentPending) _developmentPending = Guid.Empty;
        if (state.Partial) { if (state.Fixture != null) AppendDevelopmentFixture(state.Fixture); UpdateDevelopmentButtons(); return; }
        if (_developmentView?.Owner != state.Owner)
        {
            _developmentContacts.Clear(); _developmentFixtures.Clear(); _developmentDeveloper = "";
            DevelopmentContacts.Clear(); DevelopmentOfferFixtures.Clear(); DevelopmentArbitrator.Clear();
            DevelopmentArbitrator.AddItem(Loc.GetString("nc-meta-development-no-arbitrator"), 0); DevelopmentArbitrator.SelectId(0);
        }
        var old = _developmentView?.Selected;
        var changed = old?.Id != state.Selected?.Id || old?.Revision != state.Selected?.Revision || old?.TermsHash != state.Selected?.TermsHash;
        _developmentView = state;
        if (changed) ResetDevelopmentConsent();
        if (state.Selected != null) _developmentSelection = state.Selected.Id;
        DevelopmentRows.Clear();
        foreach (var row in state.Rows.Take(8))
        {
            var item = DevelopmentRows.AddItem(Loc.GetString("nc-meta-development-row", ("subject", row.Subject), ("state", Loc.GetString(row.StateKey)), ("price", row.Price)));
            item.Selected = row.Id == _developmentSelection;
        }
        DevelopmentInbox.Clear();
        foreach (var item in state.Inbox.Take(8)) DevelopmentInbox.AddItem(item.Subject);
        DevelopmentImport.Disabled = true;
        if (state.Contacts.Count > 0 || _developmentSearchRequest != Guid.Empty && _state.AcknowledgedRequestId == _developmentSearchRequest)
        {
            _developmentSearchRequest = Guid.Empty;
            _developmentContacts.Clear(); _developmentContacts.AddRange(state.Contacts.Take(8));
            DevelopmentContacts.Clear(); DevelopmentArbitrator.Clear();
            DevelopmentArbitrator.AddItem(Loc.GetString("nc-meta-development-no-arbitrator"), 0);
            for (var index = 0; index < state.Contacts.Count; index++)
            { DevelopmentContacts.AddItem(state.Contacts[index].Label); DevelopmentArbitrator.AddItem(state.Contacts[index].Label, index + 1); }
            DevelopmentArbitrator.SelectId(0); _developmentDeveloper = "";
        }
        state.Contacts = _developmentContacts.ToList();
        if (changed)
        {
            DevelopmentDetails.Clear(); DevelopmentFrozenFixtures.Clear(); DevelopmentAction.Clear(); _developmentActions.Clear();
            if (state.Selected is { } selected)
            {
                DevelopmentSubject.Text = selected.Subject;
                SetDevelopmentText(DevelopmentTerms, selected.Requirements + "\n" + selected.FailureConditions +
                    (selected.DisputeReason.Length == 0 ? "" : "\n" + Loc.GetString("nc-meta-development-dispute-record", ("reason", selected.DisputeReason))));
                DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-parties", ("customer", selected.Customer), ("developer", selected.Developer)), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString(selected.StateKey), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-price", ("amount", selected.Price)), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString(selected.TestStatusKey), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString(selected.SourceCopy ? "nc-meta-development-source-copy" : "nc-meta-development-binary"), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString(selected.AutomaticAcceptance ? "nc-meta-development-auto-policy" : "nc-meta-development-manual-policy"), selectable: false);
                DevelopmentDetails.AddItem(selected.Arbitrator.Length == 0 ? Loc.GetString("nc-meta-development-no-arbitrator") :
                    Loc.GetString("nc-meta-development-arbitrator", ("party", selected.Arbitrator)), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-due", ("minutes", Math.Max(0, (selected.DueTicks - selected.ActiveWorldTicks) / TimeSpan.TicksPerMinute))), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-support", ("minutes", selected.SupportMinutes)), selectable: false);
                DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-limits", ("resident", selected.ResidentBytes), ("memory", selected.ProcessBytes), ("storage", selected.PackageBytes)), selectable: false);
                foreach (var target in selected.Targets.Take(3)) DevelopmentDetails.AddItem(target, selectable: false);
                foreach (var scope in selected.Scopes.Take(64)) DevelopmentDetails.AddItem(Loc.GetString("nc-meta-market-scope", ("scope", scope)), selectable: false);
                foreach (var party in selected.ConsentedParties) DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-party-consented", ("party", party)), selectable: false);
                if (selected.ResolutionHash.Length > 0) DevelopmentDetails.AddItem(Loc.GetString("nc-meta-development-resolution", ("customer", selected.CustomerAmount),
                    ("developer", selected.DeveloperAmount), ("transfer", Loc.GetString(selected.TransferDelivery ? "nc-meta-development-transfer-yes" : "nc-meta-development-transfer-no"))), selectable: false);
                foreach (var fixture in selected.Fixtures.Take(16)) DevelopmentFrozenFixtures.AddItem(fixture);
                _developmentActions.AddRange(selected.Actions.Take(16));
                for (var index = 0; index < _developmentActions.Count; index++) DevelopmentAction.AddItem(Loc.GetString(DevelopmentActionKey(_developmentActions[index])), index);
                if (_developmentActions.Count > 0) DevelopmentAction.SelectId(0);
            }
            else { DevelopmentSubject.Text = ""; SetDevelopmentText(DevelopmentTerms, ""); }
        }
        // Rebuild only when the offered choices changed, keeping real native multi-selection intact.
        if (!_developmentDevices.Select(value => value.DeviceId).SequenceEqual(_state.Devices.Take(64).Select(value => value.DeviceId)))
        {
            _developmentDevices.Clear(); _developmentDevices.AddRange(_state.Devices.Take(64)); DevelopmentTargets.Clear();
            foreach (var device in _developmentDevices) DevelopmentTargets.AddItem(device.Name);
        }
        if (!_developmentFixtures.Select(file => (file.FileId, file.Revision, file.Sha256)).SequenceEqual(state.Fixtures.Take(16).Select(file => (file.FileId, file.Revision, file.Sha256))))
        {
            _developmentFixtures.Clear(); _developmentFixtures.AddRange(state.Fixtures.Take(16)); DevelopmentOfferFixtures.Clear();
            foreach (var file in _developmentFixtures) DevelopmentOfferFixtures.AddItem(file.Path);
        }
        UpdateDevelopmentButtons();
        if (state.Fixture != null) AppendDevelopmentFixture(state.Fixture);
        if (state.Prompt is { } prompt) ShowDevelopmentPrompt(prompt);
    }
    private void AppendDevelopmentFixture(NCMetaDevelopmentFixtureChunk chunk)
    {
        var selected = _developmentView?.Selected;
        if (chunk.Path != _developmentFixturePath || chunk.TotalBytes is < 1 or > 65536 ||
            chunk.ContractId != _developmentFixtureContract || chunk.Revision != _developmentFixtureRevision ||
            selected == null || selected.Id != chunk.ContractId || selected.Revision != chunk.Revision) return;
        if (chunk.Offset == 0) { _developmentFixtureBytes = new byte[chunk.TotalBytes]; _developmentFixtureReceived = 0; _developmentFixtureHash = chunk.Sha256; }
        if (_developmentFixtureBytes == null || chunk.Offset != _developmentFixtureReceived || _developmentFixtureBytes.Length != chunk.TotalBytes || chunk.Sha256 != _developmentFixtureHash) return;
        var bytes = Encoding.UTF8.GetBytes(chunk.Source);
        if (_developmentFixtureReceived + bytes.Length > _developmentFixtureBytes.Length) return;
        bytes.CopyTo(_developmentFixtureBytes, _developmentFixtureReceived); _developmentFixtureReceived += bytes.Length;
        if (chunk.Complete)
        {
            if (_developmentFixtureReceived == _developmentFixtureBytes.Length &&
                Content.Client._NC.Meta.Editor.NCMetaSourceHash.ToHexStringLower(Content.Client._NC.Meta.Editor.NCMetaSourceHash.HashData(_developmentFixtureBytes)) == _developmentFixtureHash)
                SetDevelopmentText(DevelopmentFixtureText, Content.Client._NC.Meta.Editor.NCMetaSourceHash.DecodeUtf8(_developmentFixtureBytes));
            _developmentFixtureBytes = null;
        }
        else SendDevelopment(DevelopmentCommand(NCMetaDevelopmentAction.ReadFixture) with { Text = chunk.Path, Utf8Offset = _developmentFixtureReceived });
    }
    private void ShowDevelopmentPrompt(NCMetaDevelopmentPrompt prompt)
    {
        _developmentDialog?.Dispose();
        var window = new NCMetaDevelopmentConfirmationWindow(prompt); _developmentDialog = window;
        window.Decided += accept =>
        {
            if (_developmentView?.Selected is not { } selected || selected.Id != prompt.ContractId || selected.Revision != prompt.Revision) return;
            SendDevelopment(new(prompt.Funding ? NCMetaDevelopmentAction.ConfirmFunding : NCMetaDevelopmentAction.ConfirmAcceptance,
                "", prompt.ContractId, prompt.Revision, prompt.TermsHash, prompt.DeliveryId, prompt.ReceiptId, prompt.Nonce, accept));
        };
        window.OnClose += () => { if (ReferenceEquals(_developmentDialog, window)) _developmentDialog = null; window.Dispose(); };
        window.OpenCentered();
    }
    private void ResetDevelopmentConsent()
    {
        DevelopmentConsent.Pressed = DevelopmentAutoConsent.Pressed = false;
        _developmentFixtureBytes = null; SetDevelopmentText(DevelopmentFixtureText, "");
        _developmentFixtureContract = Guid.Empty; _developmentFixturePath = _developmentFixtureHash = "";
        _developmentDialog?.Dispose(); _developmentDialog = null;
    }
    private void UpdateDevelopmentButtons()
    {
        var pending = _developmentPending != Guid.Empty;
        DevelopmentRows.SelectMode = pending ? ItemList.ItemListSelectMode.None : ItemList.ItemListSelectMode.Single;
        DevelopmentRefresh.Disabled = DevelopmentOffer.Disabled = DevelopmentContactSearch.Disabled = pending;
        DevelopmentPrevious.Disabled = pending || (_developmentView?.Offset ?? 0) == 0;
        DevelopmentNext.Disabled = pending || _developmentView?.HasMore != true;
        var action = DevelopmentAction.ItemCount > 0 && DevelopmentAction.SelectedId >= 0 && DevelopmentAction.SelectedId < _developmentActions.Count
            ? _developmentActions[DevelopmentAction.SelectedId] : NCMetaDevelopmentAction.Read;
        var needsConsent = action is NCMetaDevelopmentAction.Consent or NCMetaDevelopmentAction.ConsentResolution or NCMetaDevelopmentAction.Arbitrate;
        var selected = _developmentView?.Selected;
        DevelopmentConsent.Text = Loc.GetString(action is NCMetaDevelopmentAction.ConsentResolution or NCMetaDevelopmentAction.Arbitrate
            ? "nc-meta-development-resolution-consent" : "nc-meta-development-consent");
        DevelopmentExecute.Disabled = pending || selected == null || action == NCMetaDevelopmentAction.Read ||
            needsConsent && !DevelopmentConsent.Pressed || action == NCMetaDevelopmentAction.Consent && selected.AutomaticAcceptance && !DevelopmentAutoConsent.Pressed;
    }
    private void TickDevelopment(float seconds)
    {
        if (_developmentPending == Guid.Empty) return;
        _developmentWait += seconds;
        if (_developmentWait < 15) return;
        _developmentPending = Guid.Empty; UpdateDevelopmentButtons(); Status.Text = Loc.GetString("nc-meta-development-unconfirmed");
    }
    private void RejectDevelopment(Guid acknowledged)
    {
        if (_developmentPending == Guid.Empty || _developmentPending != acknowledged) return;
        _developmentPending = Guid.Empty; UpdateDevelopmentButtons();
    }
    private static void SetDevelopmentText(TextEdit control, string text)
    {
        if (Rope.Collapse(control.TextRope) == text) return;
        var cursor = control.CursorPosition;
        var selection = control.SelectionStart;
        control.TextRope = new Rope.Leaf(text);
        control.CursorPosition = cursor with { Index = Boundary(cursor.Index) };
        control.SelectionStart = selection with { Index = Boundary(selection.Index) };
        int Boundary(int index)
        {
            index = Math.Clamp(index, 0, text.Length);
            if (index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])) index--;
            return index;
        }
    }
    private static string DevelopmentActionKey(NCMetaDevelopmentAction action) => action switch
    {
        NCMetaDevelopmentAction.Consent => "nc-meta-development-action-consent", NCMetaDevelopmentAction.QuoteFunding => "nc-meta-development-action-fund",
        NCMetaDevelopmentAction.Deliver => "nc-meta-development-action-deliver", NCMetaDevelopmentAction.Test => "nc-meta-development-action-test",
        NCMetaDevelopmentAction.QuoteAcceptance => "nc-meta-development-action-accept", NCMetaDevelopmentAction.Dispute => "nc-meta-development-action-dispute",
        NCMetaDevelopmentAction.ProposeResolution => "nc-meta-development-action-propose", NCMetaDevelopmentAction.ConsentResolution => "nc-meta-development-action-resolve",
        NCMetaDevelopmentAction.Arbitrate => "nc-meta-development-action-arbitrate", NCMetaDevelopmentAction.RefundUndelivered => "nc-meta-development-action-refund",
        _ => "nc-meta-development-action-withdraw",
    };
}
