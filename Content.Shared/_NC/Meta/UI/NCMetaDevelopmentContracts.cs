// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

[Serializable, NetSerializable]
public enum NCMetaDevelopmentAction : byte
{
    Read, SearchContacts, Offer, Consent, QuoteFunding, ConfirmFunding, Deliver, Test,
    QuoteAcceptance, ConfirmAcceptance, Dispute, ProposeResolution, ConsentResolution,
    Arbitrate, RefundUndelivered, Withdraw, ImportSource, ReadFixture,
}

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentOffer(string Customer, string Developer, string Subject,
    string Requirements, string FailureConditions, long Price, int DeadlineMinutes, int SupportMinutes,
    bool AutomaticAcceptance, bool SourceCopy, List<string> DeviceIds, List<Guid> FixtureFiles,
    List<string> MaximumScopes, long ResidentBytes, long ProcessBytes, long PackageBytes, string? Arbitrator = null);

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentRequest(NCMetaDevelopmentAction Action, string ActingParty,
    Guid ContractId = default, long ExpectedRevision = 0, string TermsHash = "", Guid DeliveryId = default,
    Guid ReceiptId = default, Guid Nonce = default, bool Confirm = false, bool AutomaticConsent = false,
    Guid ProjectId = default, Guid ReleaseId = default, string Text = "", long CustomerAmount = 0,
    long DeveloperAmount = 0, bool TransferDelivery = false, string ResolutionHash = "",
    int Offset = 0, NCMetaDevelopmentOffer? Offer = null, int Utf8Offset = 0);

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentFixtureChunk(string Path, string Source, int Offset, int TotalBytes, string Sha256, bool Complete,
    Guid ContractId, long Revision);

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentRow(Guid Id, long Revision, string Subject, string Customer,
    string Developer, string StateKey, long Price);

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentDetails(Guid Id, long Revision, string TermsHash, string Subject,
    string Requirements, string FailureConditions, string Customer, string Developer, string StateKey,
    long Price, long ActiveWorldTicks, long DueTicks, int SupportMinutes, bool AutomaticAcceptance,
    bool SourceCopy, List<string> Targets, List<string> Scopes, long ResidentBytes, long ProcessBytes,
    long PackageBytes, List<string> Fixtures, List<NCMetaDevelopmentAction> Actions, Guid DeliveryId,
    Guid ReceiptId, string TestStatusKey, string ResolutionHash, long CustomerAmount, long DeveloperAmount,
    bool TransferDelivery, List<string> ConsentedParties, string Arbitrator = "", string DisputeReason = "");

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentPrompt(Guid Nonce, Guid ContractId, long Revision, string TermsHash,
    Guid DeliveryId, Guid ReceiptId, bool Funding, long Amount, string Customer, string Developer,
    bool SourceCopy, bool AutomaticAcceptance, int Seconds);

[Serializable, NetSerializable]
public sealed record NCMetaDevelopmentInboxRow(Guid ClaimId, string Subject, bool CanImport);

[Serializable, NetSerializable]
public sealed class NCMetaDevelopmentState
{
    public List<NCMetaDevelopmentRow> Rows = [];
    public NCMetaDevelopmentDetails? Selected;
    public List<NCMetaDevelopmentInboxRow> Inbox = [];
    public List<NCMetaSoftwareContactSummary> Contacts = [];
    public NCMetaDevelopmentPrompt? Prompt;
    public int Offset;
    public bool HasMore;
    public string Owner = "";
    public List<NCMetaFileSummary> Fixtures = [];
    public NCMetaDevelopmentFixtureChunk? Fixture;
    public bool Partial;
}
