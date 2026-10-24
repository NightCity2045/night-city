// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Linq;
using System.Text;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private string _softwareBuyerParty = "";
    private Guid _softwareResaleLicense;
    private long _softwareResaleRevision;

    private NCMetaSoftwareResaleSummary? SoftwareResale => _commerce?.Resales.FirstOrDefault(offer =>
        offer.LicenseId == _softwareResaleLicense && offer.OfferRevision == _softwareResaleRevision);

    private void InitializeSoftwareResale()
    {
        TabContainer.SetTabTitle(SoftwareTransferPanel, Loc.GetString("nc-meta-market-transfer-tab"));
        SoftwarePanels.OnTabChanged += tab =>
        {
            if (tab == 3 && _commerce?.Contacts.Count == 0) SearchSoftwareContacts();
        };
        SoftwareContactSearch.OnPressed += _ => SearchSoftwareContacts();
        SoftwareContactQuery.OnTextEntered += _ => SearchSoftwareContacts();
        SoftwareContacts.OnItemSelected += args =>
        {
            if (_applying || _commerce is null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(16, _commerce.Contacts.Count)) return;
            _softwareBuyerParty = _commerce.Contacts[args.ItemIndex].Party;
            UpdateSoftwareResaleButtons();
        };
        SoftwareResaleOffer.OnPressed += _ =>
        {
            if (!CanOfferSoftwareResale() || SoftwareLicense is not { } license ||
                !long.TryParse(SoftwareResalePrice.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var price) || price is < 0 or > 1000000)
            { Status.Text = Loc.GetString("nc-meta-market-transfer-invalid"); return; }
            CommerceRequest(NCMetaWorkspaceOperation.OfferSoftwareResale, new NCMetaSoftwareOfferResaleRequest(license.LicenseId, _softwareBuyerParty, price));
        };
        SoftwareResalePrevious.OnPressed += _ => RefreshCommerce(resaleOffset: Math.Max(0, (_commerce?.ResaleOffset ?? 0) - 8));
        SoftwareResaleNext.OnPressed += _ => RefreshCommerce(resaleOffset: (_commerce?.ResaleOffset ?? 0) + 8);
        SoftwareResaleRefresh.OnPressed += _ => RefreshCommerce();
        SoftwareResales.OnItemSelected += args =>
        {
            if (_applying || _commerce is null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(8, _commerce.Resales.Count)) return;
            var offer = _commerce.Resales[args.ItemIndex];
            _softwareResaleLicense = offer.LicenseId;
            _softwareResaleRevision = offer.OfferRevision;
            SoftwareResaleConsent.Pressed = false;
            UpdateSoftwareResaleDetails();
        };
        SoftwareResaleConsent.OnToggled += _ => UpdateSoftwareResaleButtons();
        SoftwareResaleAccept.OnPressed += _ =>
        {
            if (!SoftwareResaleConsent.Pressed || SoftwareResale is not { CanAccept: true } offer || offer.ExpiresInSeconds <= 0) return;
            SoftwareResaleConsent.Pressed = false;
            CommerceRequest(NCMetaWorkspaceOperation.AcceptSoftwareResale, new NCMetaSoftwareAcceptResaleRequest(offer.LicenseId, offer.OfferRevision));
        };
        UpdateSoftwareResaleButtons();
    }

    private void SearchSoftwareContacts()
    {
        var query = SoftwareContactQuery.Text.Trim();
        if (query.Length > 64 || Encoding.UTF8.GetByteCount(query) > 256)
        { Status.Text = Loc.GetString("nc-meta-market-transfer-invalid"); return; }
        CommerceRequest(NCMetaWorkspaceOperation.SearchSoftwareContacts, new NCMetaSoftwareContactRequest(query));
    }

    private void ApplySoftwareResale()
    {
        if (_commerce is not { } state) return;
        SoftwareContacts.Clear();
        foreach (var contact in state.Contacts.Take(16))
        {
            var item = SoftwareContacts.AddItem(Loc.GetString(contact.IsContact ? "nc-meta-market-contact-saved" : "nc-meta-market-contact-entry",
                ("name", contact.Label), ("address", contact.Address)));
            item.Selected = contact.Party == _softwareBuyerParty;
        }
        if (!state.Contacts.Take(16).Any(contact => contact.Party == _softwareBuyerParty)) _softwareBuyerParty = "";
        SoftwareContactNotice.Text = Loc.GetString(state.ContactsQuery.Length == 0 ? "nc-meta-market-contact-private" : "nc-meta-market-contact-results",
            ("query", state.ContactsQuery));
        SoftwareResales.Clear();
        foreach (var offer in state.Resales.Take(8))
        {
            var item = SoftwareResales.AddItem(Loc.GetString("nc-meta-market-resale-entry", ("name", offer.ProductName),
                ("seller", offer.SellerLabel), ("price", offer.Price)));
            item.Selected = offer.LicenseId == _softwareResaleLicense && offer.OfferRevision == _softwareResaleRevision;
        }
        SoftwareResalePrevious.Disabled = state.ResaleOffset <= 0;
        SoftwareResaleNext.Disabled = !state.ResaleHasNext;
        SoftwareOutgoingResale.Text = state.OutgoingResale is { } outgoing ? Loc.GetString("nc-meta-market-resale-outgoing",
            ("name", outgoing.ProductName), ("buyer", outgoing.BuyerLabel), ("price", outgoing.Price), ("seconds", outgoing.ExpiresInSeconds)) : "";
        SoftwareOutgoingResale.ToolTip = SoftwareOutgoingResale.Text;
        if (SoftwareResale is null) SoftwareResaleConsent.Pressed = false;
        UpdateSoftwareResaleDetails();
    }

    private void UpdateSoftwareResaleDetails()
    {
        SoftwareResaleDetails.Clear();
        if (SoftwareResale is { } offer)
        {
            SoftwareResaleDetails.AddItem(Loc.GetString("nc-meta-market-resale-entry", ("name", offer.ProductName),
                ("seller", offer.SellerLabel), ("price", offer.Price)), selectable: false);
            SoftwareResaleDetails.AddItem(Loc.GetString("nc-meta-market-resale-recipient", ("buyer", offer.BuyerLabel),
                ("seconds", offer.ExpiresInSeconds)), selectable: false);
            foreach (var term in SoftwareTerms(offer.Terms)) SoftwareResaleDetails.AddItem(term, selectable: false);
            SoftwareResaleDetails.AddItem(Loc.GetString("nc-meta-market-resale-revocation"), selectable: false);
        }
        UpdateSoftwareResaleButtons();
    }

    private bool CanOfferSoftwareResale() => SoftwareLicense is { Revoked: false, Illegal: false } license &&
        license.Terms.AllowResale && license.Terms.Transfer == NCMetaSoftwareTransferPolicy.Portable &&
        _commerce?.Contacts.Take(16).Any(contact => contact.Party == _softwareBuyerParty && contact.Party != license.Owner) == true;

    private void UpdateSoftwareResaleButtons()
    {
        SoftwareTransferLicense.Text = SoftwareLicense is { } license
            ? Loc.GetString("nc-meta-market-transfer-license", ("name", license.ProductName), ("owner", license.OwnerLabel))
            : Loc.GetString("nc-meta-market-transfer-select-license");
        SoftwareTransferLicense.ToolTip = SoftwareTransferLicense.Text;
        SoftwareResaleOffer.Disabled = !CanOfferSoftwareResale();
        SoftwareResaleAccept.Disabled = !SoftwareResaleConsent.Pressed || SoftwareResale is not { CanAccept: true, ExpiresInSeconds: > 0 };
    }
}
