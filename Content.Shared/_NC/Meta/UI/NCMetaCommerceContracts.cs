// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

[Serializable, NetSerializable]
public enum NCMetaSoftwareSourcePolicy : byte { Closed, Licensee, Open }
[Serializable, NetSerializable]
public enum NCMetaSoftwareTransferPolicy : byte { None, WithDevice, Portable }
[Serializable, NetSerializable]
public enum NCMetaSoftwareOfflinePolicy : byte { ActivatedPerpetual, GracePeriod, OnlineRequired }
[Serializable, NetSerializable]
public enum NCMetaSoftwareUpdatePolicy : byte { SameMajor, SupportedReleases }
[Serializable, NetSerializable]
public enum NCMetaSoftwareLicenseScope : byte { Device, Organization }

[Serializable, NetSerializable]
public sealed record NCMetaSoftwareTerms(
    NCMetaSoftwareSourcePolicy Source = NCMetaSoftwareSourcePolicy.Closed,
    NCMetaSoftwareTransferPolicy Transfer = NCMetaSoftwareTransferPolicy.WithDevice,
    NCMetaSoftwareOfflinePolicy Offline = NCMetaSoftwareOfflinePolicy.ActivatedPerpetual,
    NCMetaSoftwareUpdatePolicy Updates = NCMetaSoftwareUpdatePolicy.SameMajor,
    long OfflineGraceSeconds = 0, long SupportSeconds = 0, bool AllowResale = true,
    bool AllowLibraryBuild = false, bool AllowOrganizationSeats = true, bool AllowRedistribution = false);

[Serializable, NetSerializable]
public sealed record NCMetaSoftwareCatalogItem(Guid ProductId, int Major, string Name, string PublisherName, long Price);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareReleaseSummary(Guid ReleaseId, string Version, string ArtifactDigest, string RuntimeAbi,
    long ResidentBytes, List<string> RequiredScopes, List<string> RequiredInterfaces, string Changelog,
    List<NCMetaLinkSummary>? Links = null, long ProcessMemoryBytes = 0, long PackageBytes = 0,
    List<NCMetaSoftwareCompatibilitySummary>? Compatibility = null);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareProductDetails(Guid ProductId, int Major, string Name, string PublisherName,
    string Description, long Price, NCMetaSoftwareTerms Terms, List<NCMetaSoftwareReleaseSummary> Releases,
    bool CanReadSource = false, bool CanPublish = false, List<string>? SourcePaths = null);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareOwnerSummary(string Party, string Label, bool Organization);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareSeatSummary(string DeviceId, string Label, bool CanRelease);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareLicenseSummary(Guid LicenseId, Guid ProductId, int Major, string ProductName,
    string Owner, string OwnerLabel, NCMetaSoftwareLicenseScope Scope, int SeatLimit, List<NCMetaSoftwareSeatSummary> Seats,
    NCMetaSoftwareTerms Terms, bool Revoked, bool Illegal, bool CanAssign, bool CanReadSource,
    int IllegalExpiresInSeconds = 0);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareQuoteSummary(Guid QuoteId, Guid ProductId, Guid ReleaseId, string ProductName,
    string Version, string OwnerLabel, string DeviceLabel, long Price, int SeatCount, int ExpiresInSeconds,
    NCMetaSoftwareTerms Terms, bool Consumed = false, List<NCMetaSoftwareCompatibilitySummary>? Compatibility = null,
    string SlotKind = "User");
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareCompatibilitySummary(string DeviceId, string DeviceLabel, string HardwareName,
    bool Compatible, List<string> MissingInterfaces, long RequiredMemoryBytes, long AvailableMemoryBytes,
    long RequiredStorageBytes, long AvailableStorageBytes, string MessageKey, string SlotKind = "User");
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareSourceContent(Guid ProductId, Guid ReleaseId, string Path, string Source,
    string Sha256, int Utf8Offset = 0, bool Complete = true, int TotalUtf8Bytes = 0);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareContactSummary(string Party, string Label, string Address, bool IsContact);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareResaleSummary(Guid LicenseId, long OfferRevision, string ProductName,
    string SellerLabel, string BuyerLabel, long Price, int ExpiresInSeconds, NCMetaSoftwareTerms Terms, bool CanAccept);

/// <summary>Private bounded software projection. Null on an ordinary workspace reply means unchanged.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaCommerceState
{
    public List<NCMetaSoftwareCatalogItem> Catalog = [];
    public int CatalogOffset;
    public bool CatalogHasNext;
    public NCMetaSoftwareProductDetails? Product;
    public List<NCMetaSoftwareLicenseSummary> Licenses = [];
    public int LicenseOffset;
    public bool LicenseHasNext;
    public List<NCMetaSoftwareOwnerSummary> Owners = [];
    public NCMetaSoftwareQuoteSummary? Quote;
    public NCMetaSoftwareSourceContent? Source;
    public List<NCMetaSoftwareContactSummary> Contacts = [];
    public string ContactsQuery = "";
    public List<NCMetaSoftwareResaleSummary> Resales = [];
    public int ResaleOffset;
    public bool ResaleHasNext;
    public NCMetaSoftwareResaleSummary? OutgoingResale;
}

// These DTOs are bounded requests carried by the existing workspace Source JSON field, never actor authority.
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareBrowseRequest(int CatalogOffset = 0, int LicenseOffset = 0, string LicenseOwner = "", int ResaleOffset = 0) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareSelection(Guid ProductId, int Major, Guid ReleaseId = default, string SlotKind = "User") : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareQuoteRequest(Guid ProductId, int Major, Guid ReleaseId,
    string LicenseOwner = "", NCMetaSoftwareLicenseScope Scope = NCMetaSoftwareLicenseScope.Device, int SeatCount = 1,
    string SlotKind = "User") : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwarePurchaseRequest(Guid QuoteId) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareBinding(string Slot, string DeviceId, string ContractHash, List<string> Scopes);
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareInstallRequest(Guid ProductId, Guid ReleaseId, List<NCMetaSoftwareBinding> Bindings,
    bool ScopeConsent, List<string> ApprovedScopes) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareSeatRequest(Guid LicenseId, string DeviceId) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareSourceRequest(Guid ProductId, Guid ReleaseId, string Path, Guid? LicenseId = null,
    string LicenseOwner = "") : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwarePublishRequest(Guid ProductId, string Name, string Description, string Changelog,
    long Price, NCMetaSoftwareTerms Terms, string PublisherOwner = "") : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareContactRequest(string Query) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareOfferResaleRequest(Guid LicenseId, string BuyerParty, long Price) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareAcceptResaleRequest(Guid LicenseId, long OfferRevision) : NCMetaWorkspacePayload;
[Serializable, NetSerializable]
public sealed record NCMetaSoftwareDiscardCopyRequest(Guid LicenseId) : NCMetaWorkspacePayload;
