// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Commerce;

public sealed record NCMetaSoftwarePublication(Guid ProductId, int Major, NCMetaRelease Release, long Revision)
{
    public bool RedistributionApproved { get; init; }
}
public sealed record NCMetaSoftwareProductIdentity(Guid Id, string Publisher, Guid ProjectId, long Revision);
public sealed record NCMetaSoftwareReleaseLocator(Guid ProductId, Guid ReleaseId, int Major, long Revision);
public sealed record NCMetaLicensedRelease(Guid ProjectId, NCMetaRelease Release, MetaSoftwareRelease Descriptor,
    MetaSoftwareLicense? Entitlement)
{
    public bool DependenciesRedistributionApproved { get; init; }
}
public sealed record NCMetaSoftwareQuote(Guid Id, string Buyer, string LicenseOwner, Guid ProductId, int Major,
    Guid ReleaseId, string ArtifactDigest, MetaLicenseScope Scope, int SeatCount, string[] DeviceIds,
    long Price, string TermsFingerprint, long ProductRevision, long ExpiresAtTicks, bool Consumed,
    Guid LicenseId, long Revision)
{
    public MetaSoftwareCompatibility[] Compatibility { get; init; } = [];
    public string SlotKind { get; init; } = "User";
    public bool SlotAware { get; init; }
    public int? PoolSlot { get; init; }
}
public sealed record NCMetaSoftwareOwnerIndex(string Owner, Guid[] Licenses, long Revision)
{
    public Dictionary<string, Guid[]> ProductLicenses { get; init; } = [];
}
public sealed record NCMetaSoftwareProductAccess(MetaSoftwareProduct Product, MetaSoftwareLicense? License,
    bool CanPublish, string[] SourcePaths);
public sealed record NCMetaSoftwareDeviceIndex(string DeviceId, Dictionary<string, Guid> Licenses, long Revision);
public sealed record NCMetaSoftwareCatalogEntry(Guid ProductId, int Major);
public sealed record NCMetaSoftwareCatalog(NCMetaSoftwareCatalogEntry[] Products, long Revision);
public sealed record NCMetaSoftwareCommand(string Fingerprint, Guid LicenseId, Guid TransactionId, long Revision)
{
    public NCMetaSoftwareResaleOffer? ResaleOffer { get; init; }
}
public sealed record NCMetaSoftwareOutcome(bool Success, string MessageKey, MetaSoftwareProduct? Product = null,
    NCMetaSoftwareQuote? Quote = null, MetaSoftwareLicense? License = null, string? Source = null, NCMetaSoftwareResaleOffer? Resale = null);
public sealed record NCMetaSoftwareResaleOffer(Guid LicenseId, string Seller, string Buyer, long LicenseRevision,
    long Price, long ExpiresAtTicks, bool Consumed, Guid TransactionId, long Revision);
public sealed record NCMetaSoftwareResaleRecipientIndex(string Owner, Guid[] Licenses, long Revision);
public sealed record NCMetaSoftwareResaleListing(NCMetaSoftwareResaleOffer Offer, MetaSoftwareProduct Product, MetaSoftwareTerms Terms);
public sealed record NCMetaSoftwareRecipient(string Party, string DisplayName, bool Organization);
public sealed record NCMetaSoftwareActivationAdmission(NCMetaLicensedRelease LicensedRelease,
    Content.Server.Database.NCPersistentAggregateWrite? ActivationWrite, ulong DeviceAuthorityEpoch)
{
    public Guid? RequiredInheritedInstallationId { get; init; }
}
public sealed record NCMetaDeviceTransferPreparation(string DeviceId, string PreviousOwner, string NewOwner,
    ulong DeviceEpoch, ulong ExpectedAuthorityEpoch, ulong NextAuthorityEpoch, Guid TransferId, string Fingerprint,
    Guid[] TransferredLicenses, Content.Server.Database.NCPersistentAggregateWrite[] Writes)
{
    public Guid[] PreserveInstallationIds { get; init; } = [];
}
public sealed record NCMetaInheritedSoftwareGrant(Guid InstallationId, Guid LicenseId, Guid AllocationId,
    Guid ProductId, int Major, Guid ReleaseId, string ArtifactDigest, long? ActivatedAtTicks, long? LastOnlineTicks);
public sealed record NCMetaSoftwareInheritance(string DeviceId, string Owner, ulong AuthorityEpoch,
    Guid TransferId, long AtTicks, NCMetaInheritedSoftwareGrant[] Images, long Revision);
