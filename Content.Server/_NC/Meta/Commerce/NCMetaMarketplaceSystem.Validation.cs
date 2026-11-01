// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Commerce;
using Content.Meta.Hosting;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private static bool ValidParty(string? value) => value is { Length: <= 96 } && NCPartyRef.TryParse(value, out _);
    private static bool ValidRevision(long revision) => revision is > 0 and < long.MaxValue;
    private static bool ValidText(string? value, int bytes, bool required = false) => value != null &&
        (!required || value.Length > 0) && Encoding.UTF8.GetByteCount(value) <= bytes && !value.Contains('\0');
    private static bool ValidRelease(MetaSoftwareRelease release, int major) => release is
        { Id: var id, ResidentBytes: > 0, PublishedAtTicks: >= 0, RequiredScopes.Length: <= 64, RequiredInterfaces.Length: <= 64 } &&
        id != Guid.Empty && release.MinimumProcessMemoryBytes > 0 && release.MaximumConstantStringBytes is >= 0 and <= 65536 && release.PackageBytes is > 0 and <= 33554432 &&
        MetaSoftwarePolicy.Digest(release.ArtifactDigest) && MetaSoftwarePolicy.Digest(release.ContainerDigest) && MetaSoftwarePolicy.Digest(release.ApiCatalogHash) &&
        MetaSoftwareVersion.TryParse(release.Version, out var version) && version.Major == major &&
        ValidText(release.PackageIdentity, 128, true) && ValidText(release.RuntimeAbi, 64, true) && ValidText(release.Changelog, 2048) &&
        release.RequiredScopes.All(scope => ValidText(scope, 128, true)) && release.RequiredInterfaces.All(api => ValidText(api, 128, true)) &&
        release.RequiredScopes.Distinct(StringComparer.Ordinal).Count() == release.RequiredScopes.Length &&
        release.RequiredInterfaces.Distinct(StringComparer.Ordinal).Count() == release.RequiredInterfaces.Length;
    private static bool ValidResources(NCMetaReleaseResource[]? values) => values == null || values.Length <= 128 &&
        values.All(value => value is { Bytes: >= 0 and <= 262144 } && MetaPaths.IsValid(value.Path) && MetaSoftwarePolicy.Digest(value.Digest)) &&
        values.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool ValidPublication(NCMetaRelease release) => release is { Id: var id, SourceRevision: > 0 } && id != Guid.Empty &&
        MetaSoftwarePolicy.Digest(release.Digest) && ValidText(release.CompilerBuild, 128, true) &&
        ValidResources(release.Resources) && ValidResources(release.Sources) && (release.Dependencies == null ||
            release.Dependencies.Length <= 16 && release.Dependencies.All(dependency => dependency != null && dependency.ProductId != Guid.Empty && dependency.ReleaseId != Guid.Empty &&
                MetaSoftwarePolicy.Digest(dependency.BlobDigest) && MetaSoftwarePolicy.Digest(dependency.ArtifactDigest) && ValidText(dependency.PackageIdentity, 128, true) && MetaSoftwareVersion.TryParse(dependency.Version, out _))) &&
        (release.CommercialOrigin == null || release.CommercialOrigin.ProductId != Guid.Empty && release.CommercialOrigin.Major > 0 && release.CommercialOrigin.PublisherProjectId != Guid.Empty && release.CommercialOrigin.ReleaseId != Guid.Empty);
    private static bool ValidProductKey(string key)
    {
        var colon = key.IndexOf(':');
        return colon == 36 && MetaSoftwarePolicy.Device(key[..colon]) && int.TryParse(key[(colon + 1)..],
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major) &&
            major > 0 && Key(Guid.Parse(key[..colon]), major) == key;
    }
    private static bool ValidSnapshot(object value, string type, string id, long revision)
    {
        if (!ValidRevision(revision)) return false;
        return value switch
        {
            NCMetaSoftwareInheritance inheritance => type == InheritanceType && inheritance.DeviceId == id &&
                MetaSoftwarePolicy.Device(id) && ValidParty(inheritance.Owner) && inheritance.AuthorityEpoch >= 2 &&
                inheritance.TransferId != Guid.Empty && inheritance.AtTicks >= 0 && inheritance.Revision == revision &&
                inheritance.Images is { Length: <= 64 } && inheritance.Images.All(image => image != null && image.InstallationId != Guid.Empty &&
                    image.LicenseId != Guid.Empty && image.AllocationId != Guid.Empty && image.ProductId != Guid.Empty && image.Major > 0 &&
                    image.ReleaseId != Guid.Empty && MetaSoftwarePolicy.Digest(image.ArtifactDigest) &&
                    (image.ActivatedAtTicks == null && image.LastOnlineTicks == null || image.ActivatedAtTicks >= 0 &&
                        image.LastOnlineTicks >= image.ActivatedAtTicks && image.LastOnlineTicks <= inheritance.AtTicks)) &&
                inheritance.Images.Select(image => image.InstallationId).Distinct().Count() == inheritance.Images.Length,
            NCMetaSoftwareProductIdentity identity => type == "meta.software.identity" && id == identity.Id.ToString("D") && identity.Id != Guid.Empty &&
                identity.Revision == revision && identity.Revision == 1 && identity.ProjectId != Guid.Empty && ValidParty(identity.Publisher),
            NCMetaSoftwarePublisherImage image => type == "meta.software.publisher-image" && revision == 1 && image.Revision == revision &&
                image.ProjectId != Guid.Empty && image.ReleaseId != Guid.Empty && image.ProductId != Guid.Empty && image.Major > 0 &&
                MetaSoftwarePolicy.Digest(image.ArtifactDigest) && id == image.ProjectId.ToString("D") + ":" + image.ReleaseId.ToString("D"),
            NCMetaSoftwareCopyEvidence evidence => type == "meta.software.copy-evidence" && revision == 1 && evidence.Revision == revision &&
                evidence.Id != Guid.Empty && id == evidence.Id.ToString("D") && evidence.ActionId != Guid.Empty && evidence.SessionId != Guid.Empty &&
                evidence.LicenseId != Guid.Empty && evidence.ProductId != Guid.Empty && evidence.ReleaseId != Guid.Empty && evidence.SourceInstallationId != Guid.Empty &&
                ValidParty(evidence.Owner) && MetaSoftwarePolicy.Device(evidence.DeckId) && MetaSoftwarePolicy.Device(evidence.SourceId) && evidence.DeckId != evidence.SourceId &&
                evidence.DeckAuthority > 0 && evidence.SourceAuthority > 0 && evidence.NodeGeneration > 0 && ValidText(evidence.Architecture, 512, true) &&
                ValidText(evidence.Node, 512, true) && MetaSoftwarePolicy.Digest(evidence.ArtifactDigest) && MetaSoftwarePolicy.Digest(evidence.Fingerprint) &&
                evidence.AtTicks >= 0 && evidence.ExpiresAtTicks > evidence.AtTicks && evidence.ExpiresAtTicks - evidence.AtTicks <= MetaSoftwarePolicy.MaximumPiracyDurationTicks &&
                ValidCopyEvidence(evidence),
            MetaSoftwareProduct product => type == ProductType && product.Id != Guid.Empty && id == Key(product.Id, product.Major) &&
                product.Revision == revision && product.Major > 0 && ValidParty(product.Publisher) && product.ProjectId != Guid.Empty &&
                ValidText(product.Name, 256, true) && ValidText(product.Description, 2048) && product.Terms != null &&
                MetaSoftwarePolicy.ValidTerms(product.Terms) && product.Price is >= 0 and <= 1000000 &&
                product.Releases is { Length: > 0 and <= 64 } && product.Releases.All(release => ValidRelease(release, product.Major)) &&
                product.Releases.Select(release => release.Id).Distinct().Count() == product.Releases.Length &&
                product.Releases.Select(release => release.Version).Distinct(StringComparer.Ordinal).Count() == product.Releases.Length &&
                product.Releases.Select(release => release.PackageIdentity).Distinct(StringComparer.Ordinal).Count() == 1,
            MetaSoftwareLicense license => type == LicenseType && id == license.Id.ToString("D") && license.Revision == revision &&
                ValidParty(license.Owner) && MetaSoftwarePolicy.ValidLicense(license),
            NCMetaSoftwarePublication publication => type == PublicationType && publication.ProductId != Guid.Empty && publication.Major > 0 &&
                publication.Revision == revision && ValidPublication(publication.Release) &&
                id == Key(publication.ProductId, publication.Major) + ":" + publication.Release.Id.ToString("D"),
            NCMetaSoftwareReleaseLocator locator => type == "meta.software.release-index" && locator.ProductId != Guid.Empty && locator.ReleaseId != Guid.Empty &&
                locator.Major > 0 && locator.Revision == revision && id == locator.ProductId.ToString("D") + ":" + locator.ReleaseId.ToString("D"),
            NCMetaSoftwareOwnerIndex owner => type == OwnerType && owner.Owner == id && owner.Revision == revision && ValidParty(owner.Owner) &&
                owner.Licenses is { Length: <= 128 } && owner.Licenses.All(license => license != Guid.Empty) && owner.Licenses.Distinct().Count() == owner.Licenses.Length &&
                owner.ProductLicenses is { Count: <= 128 } && owner.ProductLicenses.All(pair => ValidProductKey(pair.Key) && pair.Value is { Length: > 0 and <= 128 } &&
                    pair.Value.All(owner.Licenses.Contains) && pair.Value.Distinct().Count() == pair.Value.Length) &&
                owner.ProductLicenses.Values.SelectMany(value => value).Distinct().Count() == owner.ProductLicenses.Values.Sum(value => value.Length),
            NCMetaSoftwareDeviceIndex device => type == DeviceType && device.DeviceId == id && MetaSoftwarePolicy.Device(device.DeviceId) &&
                device.Revision == revision && device.Licenses is { Count: <= 64 } && device.Licenses.All(pair => ValidProductKey(pair.Key) && pair.Value != Guid.Empty),
            NCMetaSoftwareQuote quote => type == QuoteType && quote.Id != Guid.Empty &&
                (quote.PoolSlot is { } slot ? slot is >= 0 and < MetaQuotePoolPolicy.Slots && id == MetaQuotePoolPolicy.SlotKey(slot) : id == quote.Id.ToString("D")) && quote.Revision == revision &&
                ValidParty(quote.Buyer) && ValidParty(quote.LicenseOwner) && quote.ProductId != Guid.Empty && quote.ReleaseId != Guid.Empty && quote.Major > 0 &&
                MetaSoftwarePolicy.Digest(quote.ArtifactDigest) && MetaSoftwarePolicy.Digest(quote.TermsFingerprint) && quote.ProductRevision > 0 &&
                quote.Price is >= 0 and <= 64000000 && quote.ExpiresAtTicks > 0 && Enum.IsDefined(quote.Scope) && quote.SeatCount is >= 1 and <= 64 &&
                quote.DeviceIds is { Length: <= 6 } && quote.DeviceIds.Length <= quote.SeatCount && quote.DeviceIds.All(MetaSoftwarePolicy.Device) &&
                quote.DeviceIds.Distinct(StringComparer.Ordinal).Count() == quote.DeviceIds.Length &&
                quote.Compatibility is { Length: <= 16 } && quote.Compatibility.All(value => value != null && MetaSoftwarePolicy.Device(value.DeviceId) &&
                    quote.DeviceIds.Contains(value.DeviceId, StringComparer.Ordinal) && ValidText(value.HardwareProfile, 128, true) && value.MissingInterfaces is { Length: <= 64 } &&
                    value.MissingInterfaces.All(api => ValidText(api, 128, true)) && value.RequiredMemoryBytes > 0 && value.AvailableMemoryBytes > 0 && value.RequiredStorageBytes > 0 && value.AvailableStorageBytes >= 0 && value.StorageRevision >= 0) &&
                (quote.Scope != MetaLicenseScope.Device || quote.SeatCount == 1 && quote.DeviceIds.Length == 1) &&
                quote.Consumed == (quote.LicenseId != Guid.Empty),
            NCMetaSoftwareCatalog catalog => type == "meta.software.catalog" && id == "public" && catalog.Revision == revision &&
                catalog.Products is { Length: <= 256 } && catalog.Products.All(entry => entry is { Major: > 0 } && entry.ProductId != Guid.Empty) &&
                catalog.Products.Select(entry => (entry.ProductId, entry.Major)).Distinct().Count() == catalog.Products.Length,
            NCMetaSoftwareCommand command => type == CommandType && Guid.TryParseExact(id, "D", out var key) && key != Guid.Empty &&
                command.Revision == revision && MetaSoftwarePolicy.Digest(command.Fingerprint) && command.LicenseId != Guid.Empty &&
                (command.ResaleOffer == null || command.ResaleOffer.LicenseId == command.LicenseId &&
                    ValidSnapshot(command.ResaleOffer, "meta.software.resale", command.LicenseId.ToString("D"), command.ResaleOffer.Revision)),
            NCMetaSoftwareResaleOffer offer => type == "meta.software.resale" && id == offer.LicenseId.ToString("D") && offer.LicenseId != Guid.Empty &&
                offer.Revision == revision && ValidParty(offer.Seller) && ValidParty(offer.Buyer) && offer.Seller != offer.Buyer &&
                offer.LicenseRevision > 0 && offer.Price is >= 0 and <= 1000000 && offer.ExpiresAtTicks > 0 &&
                offer.Consumed == (offer.TransactionId != Guid.Empty),
            NCMetaSoftwareResaleRecipientIndex recipient => type == ResaleRecipientsType && id == recipient.Owner && ValidParty(recipient.Owner) &&
                recipient.Revision == revision && recipient.Licenses is { Length: <= 128 } && recipient.Licenses.All(value => value != Guid.Empty) &&
                recipient.Licenses.Distinct().Count() == recipient.Licenses.Length,
            _ => false
        };
    }
    private static bool ValidCopyEvidence(NCMetaSoftwareCopyEvidence evidence) => evidence.Admission is { } admission &&
        admission.ActionId == evidence.ActionId && admission.SessionId == evidence.SessionId && admission.Owner == evidence.Owner &&
        admission.DeckId == evidence.DeckId && admission.DeckAuthority == evidence.DeckAuthority && admission.DeckEpoch > 0 &&
        admission.SourceId == evidence.SourceId && admission.SourceAuthority == evidence.SourceAuthority && admission.SourceEpoch > 0 &&
        ValidParty(admission.SourceOwner) && admission.SourceOwner != admission.Owner && admission.Architecture == evidence.Architecture &&
        admission.Node == evidence.Node && admission.NodeGeneration == evidence.NodeGeneration && admission.InstallationId == evidence.SourceInstallationId &&
        evidence.Fingerprint == MetaSoftwarePolicy.Fingerprint(System.Text.Json.JsonSerializer.Serialize(admission, Json), evidence.ArtifactDigest);
}
