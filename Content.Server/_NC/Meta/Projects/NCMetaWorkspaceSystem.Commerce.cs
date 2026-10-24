// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed class CommerceSelection
    {
        public string Owner = "";
        public int CatalogOffset;
        public int LicenseOffset;
        public NCMetaSoftwareSelection? Product;
        public NCMetaSoftwareQuote? Quote;
        public string DeviceId = "";
        public int ResaleOffset;
        public string ContactsQuery = "";
        public NCMetaSoftwareResaleOffer? Outgoing;
        public MetaSoftwareLicense? OutgoingLicense;
    }
    [Dependency] private NCPartyResolverSystem _commerceParties = default!;
    [Dependency] private Content.Server.Database.IServerDbManager _contactDatabase = default!;
    private readonly Dictionary<(EntityUid Terminal, EntityUid User), CommerceSelection> _commerceViews = [];
    private static readonly JsonSerializerOptions CommerceJson = new()
    {
        MaxDepth = 12,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private async Task CommerceRequestAsync(Context context, NCMetaProject project,
        NCMetaWorkspaceRequestMessage request, NCMetaWorkspaceState state)
    {
        if (!await OnMainAsync(() => IsCurrent(context) && _marketplace.CanAccessService(context.Terminal, context.User)))
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var key = (context.Terminal, context.User);
        // The session index belongs to the ECS thread. Work on an isolated snapshot
        // while awaiting storage; closing a BUI may remove its entry at any time.
        var selection = await OnMainAsync(() =>
        {
            if (!IsCurrent(context)) return null;
            if (!_commerceViews.TryGetValue(key, out var previous))
            {
                if (_commerceViews.Count >= 128) return null;
                previous = new CommerceSelection { Owner = context.Owner };
                _commerceViews.Add(key, previous);
            }
            return new CommerceSelection { Owner = previous.Owner, CatalogOffset = previous.CatalogOffset,
                LicenseOffset = previous.LicenseOffset, Product = previous.Product, Quote = previous.Quote,
                ResaleOffset = previous.ResaleOffset, ContactsQuery = previous.ContactsQuery, Outgoing = previous.Outgoing,
                OutgoingLicense = previous.OutgoingLicense,
                DeviceId = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId };
        });
        if (selection == null) { state.Notice = Loc.GetString("nc-meta-error-quota"); return; }
        T Read<T>() where T : class => JsonSerializer.Deserialize<T>(request.Source, CommerceJson) ?? throw new JsonException();
        NCMetaSoftwareOutcome? outcome = null;
        NCMetaSoftwareSourceContent? source = null;
        try
        {
            switch (request.Operation)
            {
                case NCMetaWorkspaceOperation.BrowseSoftware:
                    var browse = request.Source.Length == 0 ? new NCMetaSoftwareBrowseRequest() : Read<NCMetaSoftwareBrowseRequest>();
                    if (browse.CatalogOffset is < 0 or > 256 || browse.LicenseOffset is < 0 or > 128 ||
                        browse.CatalogOffset % 8 != 0 || browse.LicenseOffset % 8 != 0 || browse.LicenseOwner == null ||
                        browse.ResaleOffset is < 0 or > 120 || browse.ResaleOffset % 8 != 0) throw new JsonException();
                    selection.CatalogOffset = browse.CatalogOffset;
                    selection.LicenseOffset = browse.LicenseOffset;
                    selection.ResaleOffset = browse.ResaleOffset;
                    selection.Owner = browse.LicenseOwner.Length == 0 ? context.Owner : browse.LicenseOwner;
                    break;
                case NCMetaWorkspaceOperation.SearchSoftwareContacts:
                    var contacts = Read<NCMetaSoftwareContactRequest>();
                    if (contacts.Query is not { Length: <= 64 }) throw new JsonException();
                    selection.ContactsQuery = contacts.Query.Trim();
                    break;
                case NCMetaWorkspaceOperation.OfferSoftwareResale:
                    var offer = Read<NCMetaSoftwareOfferResaleRequest>();
                    outcome = await _marketplace.TryOfferResaleAsync(context.Terminal, context.User, request.RequestId,
                        offer.LicenseId, offer.BuyerParty, offer.Price);
                    selection.Outgoing = outcome.Resale;
                    selection.OutgoingLicense = outcome.License;
                    break;
                case NCMetaWorkspaceOperation.AcceptSoftwareResale:
                    var accepted = Read<NCMetaSoftwareAcceptResaleRequest>();
                    outcome = await _marketplace.TryAcceptResaleAsync(context.Terminal, context.User, request.RequestId,
                        accepted.LicenseId, accepted.OfferRevision);
                    break;
                case NCMetaWorkspaceOperation.DiscardSoftwareCopy:
                    var discarded = Read<NCMetaSoftwareDiscardCopyRequest>();
                    outcome = await _marketplace.TryDiscardSoftwareCopyAsync(context.Terminal, context.User, request.RequestId, discarded.LicenseId);
                    break;
                case NCMetaWorkspaceOperation.ReadSoftwareProduct:
                    selection.Product = Read<NCMetaSoftwareSelection>();
                    if (selection.Product.SlotKind is not ("User" or "Firmware")) throw new JsonException();
                    selection.Quote = null;
                    break;
                case NCMetaWorkspaceOperation.QuoteSoftware:
                    var quote = Read<NCMetaSoftwareQuoteRequest>();
                    if (!Enum.IsDefined(quote.Scope) || quote.LicenseOwner == null || quote.SlotKind is not ("User" or "Firmware")) throw new JsonException();
                    selection.Owner = quote.LicenseOwner.Length == 0 ? context.Owner : quote.LicenseOwner;
                    outcome = await _marketplace.TryQuoteAsync(context.Terminal, context.User, request.RequestId,
                        quote.ProductId, quote.Major, quote.ReleaseId, selection.Owner, (MetaLicenseScope) quote.Scope,
                        quote.SeatCount, quote.Scope == NCMetaSoftwareLicenseScope.Device
                            ? [request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId] : [], quote.SlotKind);
                    selection.Quote = outcome.Quote;
                    selection.Product = new(quote.ProductId, quote.Major, quote.ReleaseId, quote.SlotKind);
                    break;
                case NCMetaWorkspaceOperation.PurchaseSoftware:
                    var purchase = Read<NCMetaSoftwarePurchaseRequest>();
                    outcome = await _marketplace.TryPurchaseAsync(context.Terminal, context.User, request.RequestId, purchase.QuoteId);
                    if (outcome.Success) selection.Quote = null;
                    break;
                case NCMetaWorkspaceOperation.PublishSoftware:
                    var publish = Read<NCMetaSoftwarePublishRequest>();
                    if (publish.Terms == null || publish.PublisherOwner == null) throw new JsonException();
                    outcome = await _marketplace.TryPublishAsync(context.Terminal, context.User, request.RequestId,
                        project.Id, request.ReleaseId, new MetaSoftwareProductDraft
                        {
                            ProductId = publish.ProductId, Name = publish.Name, Description = publish.Description,
                            Changelog = publish.Changelog, Price = publish.Price, Terms = CoreTerms(publish.Terms),
                        }, publish.PublisherOwner.Length == 0 ? context.Owner : publish.PublisherOwner);
                    if (outcome.Product != null) selection.Product = new(outcome.Product.Id, outcome.Product.Major, request.ReleaseId);
                    break;
                case NCMetaWorkspaceOperation.AssignSoftwareSeat:
                case NCMetaWorkspaceOperation.ReleaseSoftwareSeat:
                    var seat = Read<NCMetaSoftwareSeatRequest>();
                    outcome = request.Operation == NCMetaWorkspaceOperation.AssignSoftwareSeat
                        ? await _marketplace.TryAssignSeatAsync(context.Terminal, context.User, request.RequestId, seat.LicenseId, seat.DeviceId)
                        : await _marketplace.TryReleaseSeatAsync(context.Terminal, context.User, request.RequestId, seat.LicenseId, seat.DeviceId);
                    break;
                case NCMetaWorkspaceOperation.InstallLicensedSoftware:
                    var install = Read<NCMetaSoftwareInstallRequest>();
                    if (install.Bindings is not { Count: <= 64 } || install.ApprovedScopes is not { Count: <= 64 } ||
                        install.Bindings.Any(binding => binding == null || binding.Scopes == null) || !install.ScopeConsent)
                        throw new JsonException();
                    var id = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
                    var licensed = await _marketplace.ResolveLicensedReleaseAsync(context.Owner, id, install.ProductId, install.ReleaseId);
                    if (licensed == null || !licensed.Descriptor.RequiredScopes.Order(StringComparer.Ordinal)
                            .SequenceEqual(install.ApprovedScopes.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
                    var host = await OnMainAsync<EntityUid?>(() => IsCurrent(context) && _devices.TryResolve(id, out var target)
                        ? target.Entity : null);
                    if (host == null) { state.Notice = Loc.GetString("nc-meta-error-device"); return; }
                    var installed = await InstallLicensedReleaseAsync(context, host.Value, id, request.RequestId,
                        install.ProductId, install.ReleaseId, install.Bindings.Select(binding => new NCMetaBinding(
                            binding.Slot, binding.DeviceId, binding.ContractHash, binding.Scopes.ToArray())).ToArray(), true);
                    state.Notice = Loc.GetString(installed.MessageKey);
                    break;
                case NCMetaWorkspaceOperation.ReadSoftwareSource:
                    var read = Read<NCMetaSoftwareSourceRequest>();
                    if (read.Path == null || read.LicenseOwner == null) throw new JsonException();
                    outcome = await _marketplace.TryReadSourceAsync(context.Terminal, context.User, read.ProductId,
                        read.ReleaseId, read.Path, read.LicenseId, read.LicenseOwner.Length == 0 ? selection.Owner : read.LicenseOwner);
                    if (outcome.Source != null)
                    {
                        var text = new SourceText(read.Path, outcome.Source);
                        if (!text.IsScalarBoundary(request.Utf8Offset)) throw new JsonException();
                        var end = Math.Min(text.Utf8.Length, request.Utf8Offset + Math.Clamp(request.ChunkBytes, 1, 32 * 1024));
                        while (!text.IsScalarBoundary(end)) end--;
                        source = new(read.ProductId, read.ReleaseId, read.Path,
                            Utf8.GetString(text.Utf8.Span[request.Utf8Offset..end]), Convert.ToHexStringLower(SHA256.HashData(text.Utf8.Span)),
                            request.Utf8Offset, end == text.Utf8.Length, text.Utf8.Length);
                    }
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
        { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
        if (outcome != null) state.Notice = Loc.GetString(outcome.MessageKey);
        if (!await OnMainAsync(() => IsCurrent(context) && _marketplace.CanAccessService(context.Terminal, context.User))) return;
        var projection = await BuildCommerceProjectionAsync(context, selection);
        projection.Source = source;
        await OnMainAsync(() =>
        {
            if (!IsCurrent(context) || !_marketplace.CanAccessService(context.Terminal, context.User) || !_commerceViews.ContainsKey(key)) return false;
            _commerceViews[key] = selection;
            state.Commerce = projection;
            return true;
        });
    }

    private async Task<NCMetaCommerceState> BuildCommerceProjectionAsync(Context context, CommerceSelection selection)
    {
        var projection = new NCMetaCommerceState { CatalogOffset = selection.CatalogOffset, LicenseOffset = selection.LicenseOffset,
            ContactsQuery = selection.ContactsQuery, ResaleOffset = selection.ResaleOffset };
        var owners = await _marketplace.ReadManageableOwnersAsync(context.Terminal, context.User);
        foreach (var (owner, organization) in owners)
            projection.Owners.Add(new(owner, await CommercePartyLabel(owner), organization));
        if (!projection.Owners.Any(owner => owner.Party == selection.Owner)) selection.Owner = context.Owner;
        foreach (var product in await _marketplace.ReadCatalogPageAsync(selection.CatalogOffset))
            projection.Catalog.Add(new(product.Id, product.Major, product.Name, await CommercePartyLabel(product.Publisher), product.Price));
        projection.CatalogHasNext = selection.CatalogOffset < 248 &&
            (await _marketplace.ReadCatalogPageAsync(selection.CatalogOffset + 8)).Length > 0;
        var licenses = await _marketplace.ReadLicensePageAsync(context.Terminal, context.User, selection.Owner, selection.LicenseOffset);
        foreach (var license in licenses)
        {
            var product = await _marketplace.ReadProductAsync(license.ProductId, license.Major);
            var seats = await OnMainAsync(() => license.Seats.Take(64).Select(seat => new NCMetaSoftwareSeatSummary(
                seat.DeviceId, _devices.TryResolve(seat.DeviceId, out var device) && device.Owner == license.Owner
                    ? Name(device.Entity) : Loc.GetString("nc-meta-software-device-offline"),
                license.Terms.Transfer != MetaLicenseTransferPolicy.None && !license.Revoked)).ToList());
            projection.Licenses.Add(new(license.Id, license.ProductId, license.Major, product?.Name ?? "",
                license.Owner, await CommercePartyLabel(license.Owner), (NCMetaSoftwareLicenseScope) license.Scope,
                license.SeatLimit, seats, UiTerms(license.Terms), license.Revoked,
                license.Provenance == MetaLicenseProvenance.NativePiracy, !license.Revoked && license.Seats.Length < license.SeatLimit,
                product != null && MetaSoftwarePolicy.CanReadSource(product, license, selection.Owner, context.Time.Ticks),
                license.IllegalExpiresAtTicks is { } expiry ? (int) Math.Clamp((expiry - context.Time.Ticks) / TimeSpan.TicksPerSecond, 0, 1800) : 0));
        }
        projection.LicenseHasNext = selection.LicenseOffset < 120 &&
            (await _marketplace.ReadLicensePageAsync(context.Terminal, context.User, selection.Owner, selection.LicenseOffset + 8)).Length > 0;
        if (selection.Product is { } selected)
        {
            var access = await _marketplace.ReadProductAccessAsync(context.Terminal, context.User, selected.ProductId,
                selected.Major, selected.ReleaseId, selection.Owner);
            if (access != null)
            {
                var product = access.Product;
                var selectedRelease = selected.ReleaseId == Guid.Empty ? product.Releases.LastOrDefault()?.Id : selected.ReleaseId;
                var releases = new List<NCMetaSoftwareReleaseSummary>();
                foreach (var release in product.Releases.TakeLast(16).Reverse())
                {
                    List<NCMetaLinkSummary>? links = null;
                    var licensed = release.Id == selectedRelease
                        ? await _marketplace.ResolveLicensedReleaseAsync(selection.Owner, selection.DeviceId, product.Id, release.Id) : null;
                    if (licensed != null && await ReadVerifiedAsync(licensed.Release) is { } artifact)
                        links = artifact.GetLinks().Select(link => new NCMetaLinkSummary(link.SlotName,
                            artifact.GetType(link.CapabilityTypeId).Name, link.ContractHash,
                            MetaApiCatalog.Default.Members.Where(member => member.Owner == artifact.GetType(link.CapabilityTypeId).Name &&
                                artifact.Manifest.RequiredScopes.Contains(member.Scope)).Select(member => member.Scope).Distinct().ToList())).ToList();
                    var compatibility = release.Id == selectedRelease ? await _marketplace.ReadCompatibilityAsync(context.Terminal,
                        context.User, product.Id, product.Major, release.Id, selection.DeviceId, selected.SlotKind) : null;
                    releases.Add(new(release.Id, release.Version, release.ArtifactDigest, release.RuntimeAbi,
                        release.ResidentBytes, release.RequiredScopes.ToList(), release.RequiredInterfaces.ToList(), release.Changelog,
                        links, release.MinimumProcessMemoryBytes, release.PackageBytes,
                        compatibility == null ? null : [await UiCompatibility(compatibility)]));
                }
                projection.Product = new(product.Id, product.Major, product.Name, await CommercePartyLabel(product.Publisher),
                    product.Description, product.Price, UiTerms(product.Terms), releases,
                    access.SourcePaths.Length > 0, access.CanPublish, access.SourcePaths.ToList());
            }
        }
        if (selection.Quote is { Consumed: false } quote && projection.Product is { } offered &&
            quote.ProductId == offered.ProductId && quote.ExpiresAtTicks > context.Time.Ticks)
        {
            var currentProduct = await _marketplace.ReadProductAsync(quote.ProductId, quote.Major);
            if (currentProduct == null || currentProduct.Revision != quote.ProductRevision ||
                MetaSoftwarePolicy.TermsFingerprint(currentProduct.Terms) != quote.TermsFingerprint) selection.Quote = null;
            var release = offered.Releases.FirstOrDefault(value => value.ReleaseId == quote.ReleaseId);
            var deviceLabel = await OnMainAsync(() => quote.DeviceIds.Length == 1 && _devices.TryResolve(quote.DeviceIds[0], out var device)
                ? Name(device.Entity) : Loc.GetString("nc-meta-software-unallocated"));
            var compatibility = new List<NCMetaSoftwareCompatibilitySummary>();
            foreach (var check in quote.Compatibility.Take(6)) compatibility.Add(await UiCompatibility(check));
            if (release != null && selection.Quote != null) projection.Quote = new(quote.Id, quote.ProductId, quote.ReleaseId, offered.Name, release.Version,
                await CommercePartyLabel(quote.LicenseOwner), deviceLabel, quote.Price, quote.SeatCount,
                (int) Math.Clamp((quote.ExpiresAtTicks - context.Time.Ticks) / TimeSpan.TicksPerSecond, 0, 300), offered.Terms,
                false, compatibility, quote.SlotKind);
        }
        if (NCPartyRef.TryParse(context.Owner, out var actor) && actor.TryGetCharacter(out var character))
            foreach (var contact in await _contactDatabase.GetNCMetaAgentRecipientsAsync(
                         new Content.Shared._NC.Identity.NCCharacterId(character.Value), selection.ContactsQuery, 16))
                projection.Contacts.Add(new(NCPartyRef.ForCharacter(new Content.Shared._NC.Identity.NCCharacterId(contact.CharacterId.Value)).ToString(),
                    contact.Alias, contact.Address, contact.IsContact));
        foreach (var listing in await _marketplace.ReadResaleOffersAsync(context.Terminal, context.User, selection.Owner, selection.ResaleOffset))
            projection.Resales.Add(await UiResale(listing.Offer, listing.Product.Name, listing.Terms, true, context.Time.Ticks));
        projection.ResaleHasNext = selection.ResaleOffset < 120 &&
            (await _marketplace.ReadResaleOffersAsync(context.Terminal, context.User, selection.Owner, selection.ResaleOffset + 8)).Length > 0;
        if (selection.Outgoing is { } outgoing && selection.OutgoingLicense is { } offeredLicense && !outgoing.Consumed &&
            outgoing.ExpiresAtTicks > context.Time.Ticks && outgoing.LicenseRevision == offeredLicense.Revision)
        {
            var product = await _marketplace.ReadProductAsync(offeredLicense.ProductId, offeredLicense.Major);
            projection.OutgoingResale = await UiResale(outgoing, product?.Name ?? "", offeredLicense.Terms, false, context.Time.Ticks);
        }
        return projection;
    }

    private async Task<NCMetaSoftwareCompatibilitySummary> UiCompatibility(MetaSoftwareCompatibility value)
    {
        var label = await OnMainAsync(() => _devices.TryResolve(value.DeviceId, out var device)
            ? Name(device.Entity) : Loc.GetString("nc-meta-software-device-offline"));
        return new(value.DeviceId, label, value.HardwareProfile, value.Compatible, value.MissingInterfaces.ToList(),
            value.RequiredMemoryBytes, value.AvailableMemoryBytes, value.RequiredStorageBytes, value.AvailableStorageBytes, value.MessageKey, value.SlotKind);
    }

    private async Task<NCMetaSoftwareResaleSummary> UiResale(NCMetaSoftwareResaleOffer offer, string name,
        MetaSoftwareTerms terms, bool canAccept, long now) => new(offer.LicenseId, offer.Revision, name,
        await CommercePartyLabel(offer.Seller), await CommercePartyLabel(offer.Buyer), offer.Price,
        (int) Math.Clamp((offer.ExpiresAtTicks - now) / TimeSpan.TicksPerSecond, 0, 120), UiTerms(terms), canAccept);

    private async Task<string> CommercePartyLabel(string owner)
    {
        if (!NCPartyRef.TryParse(owner, out var party)) return Loc.GetString("nc-meta-software-unknown-party");
        var resolved = await _commerceParties.ResolveAsync(party);
        if (!resolved.IsResolved || resolved.DisplayName.Length == 0) return Loc.GetString("nc-meta-software-unknown-party");
        var length = Math.Min(128, resolved.DisplayName.Length);
        if (length < resolved.DisplayName.Length && char.IsHighSurrogate(resolved.DisplayName[length - 1])) length--;
        return resolved.DisplayName[..length];
    }
    private static NCMetaSoftwareTerms UiTerms(MetaSoftwareTerms terms) => new(
        (NCMetaSoftwareSourcePolicy) terms.Source, (NCMetaSoftwareTransferPolicy) terms.Transfer,
        (NCMetaSoftwareOfflinePolicy) terms.Offline, (NCMetaSoftwareUpdatePolicy) terms.Updates,
        terms.OfflineGraceTicks / TimeSpan.TicksPerSecond, terms.SupportTicks / TimeSpan.TicksPerSecond,
        terms.AllowResale, terms.AllowLibraryBuild, terms.AllowOrganizationSeats, terms.AllowRedistribution);
    private static MetaSoftwareTerms CoreTerms(NCMetaSoftwareTerms terms) => new()
    {
        Source = (MetaSourcePolicy) terms.Source, Transfer = (MetaLicenseTransferPolicy) terms.Transfer,
        Offline = (MetaOfflinePolicy) terms.Offline, Updates = (MetaUpdatePolicy) terms.Updates,
        OfflineGraceTicks = checked(terms.OfflineGraceSeconds * TimeSpan.TicksPerSecond),
        SupportTicks = checked(terms.SupportSeconds * TimeSpan.TicksPerSecond), AllowResale = terms.AllowResale,
        AllowLibraryBuild = terms.AllowLibraryBuild, AllowOrganizationSeats = terms.AllowOrganizationSeats,
        AllowRedistribution = terms.AllowRedistribution,
    };
}
