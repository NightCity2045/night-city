// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Commerce;

/// <summary>Software entitlements share the existing ledger transaction with every license/index write.</summary>
public sealed partial class NCMetaMarketplaceSystem : EntitySystem
{
    private const string ProductType = "meta.software.product";
    private const string PublicationType = "meta.software.publication";
    private const string LicenseType = "meta.software.license";
    private const string QuoteType = "meta.software.quote";
    private const string OwnerType = "meta.software.owner";
    private const string DeviceType = "meta.software.device";
    private const string CommandType = "meta.software.command";
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };
    private sealed record Context(EntityUid Terminal, EntityUid User, string Actor, string DeviceId,
        ulong DeviceEpoch, ulong AuthorityEpoch, NCSessionEpoch Epoch, NCActiveWorldTime Time, NCMetaManagementProof TerminalManagement)
    {
        public Dictionary<string, NCMetaManagementProof> Management { get; } = new(StringComparer.Ordinal);
    }
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaSnapshotSystem _snapshots = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private readonly SemaphoreSlim _publicationGate = new(2, 2);
    private readonly SemaphoreSlim[] _commands = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly MetaRateLimit _ingress = new(32, 16);
    /// <summary>Read-only main-thread ingress check. Mutations also capture and recheck authority after asynchronous work.</summary>
    public bool CanAccessService(EntityUid terminal, EntityUid user) =>
        _ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) && _devices.CanManage(terminal, user) &&
        HasMarketplaceRoute(terminal) && _devices.TryGetEntry(terminal, out _) && _parties.TryResolve(user, out _) &&
        _persistence.TryCaptureTime(out _, out _);
    private Context? Capture(EntityUid terminal, EntityUid user)
    {
        if (!CanAccessService(terminal, user) || !_devices.TryGetEntry(terminal, out var device) || !_parties.TryResolve(user, out var party) ||
            !_authority.TryGetManagementProof(user, device.Owner, out var management) || !_persistence.TryCaptureTime(out var epoch, out var time) ||
            !_ingress.TryConsume(time.Ticks / TimeSpan.TicksPerMillisecond)) return null;
        return new(terminal, user, party.ToString(), device.Id, device.DeviceEpoch, device.AuthorityEpoch, epoch, time, management);
    }
    private bool Current(Context context) => _ui.IsUiOpen(context.Terminal, NCMetaUiKey.Workspace, context.User) && _devices.CanManage(context.Terminal, context.User) &&
        _authority.IsCurrent(context.TerminalManagement) && HasMarketplaceRoute(context.Terminal) && _devices.TryGetEntry(context.Terminal, out var device) && device.Id == context.DeviceId && device.DeviceEpoch == context.DeviceEpoch && device.AuthorityEpoch == context.AuthorityEpoch && _parties.TryResolve(context.User, out var actor) && actor.ToString() == context.Actor && _persistence.TryCaptureTime(out var epoch, out _) && epoch == context.Epoch;
    private bool CurrentBefore(Context context, long expiresAtTicks) => Current(context) &&
        _persistence.TryCaptureTime(out _, out var time) && time.Ticks < expiresAtTicks;
    public Task<MetaSoftwareProduct?> ReadProductAsync(Guid productId, int major) => Read<MetaSoftwareProduct>(ProductType, Key(productId, major));
    public async Task<NCMetaSoftwareOutcome> TryPublishAsync(EntityUid terminal, EntityUid user, Guid requestId,
        Guid projectId, Guid releaseId, MetaSoftwareProductDraft draft, string? publisherOwner = null)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || requestId == Guid.Empty || !await _publicationGate.WaitAsync(0)) return Failure("busy");
        try
        {
            var context = await Main(() => Capture(terminal, user));
            if (context == null || draft == null || draft.Name is not { Length: > 0 and <= 64 } || !ValidText(draft.Name, 256, true) || !ValidText(draft.Description, 2048) || !ValidText(draft.Changelog, 2048) || draft.Terms == null || draft.Price is < 0 or > 1000000 || !MetaSoftwarePolicy.ValidTerms(draft.Terms)) return Failure("input");
            var publisher = publisherOwner ?? await Main(() => _devices.TryResolve(context.DeviceId, out var host) ? host.Owner : context.Actor);
            if (!await CanActFor(context, publisher)) return Failure("access");
            var project = await _persistence.ReadProjectAsync(projectId, publisher);
            var release = project?.Releases.SingleOrDefault(release => release.Id == releaseId);
            if (project == null || release == null) return Failure("access");
            var bytes = await _database.ReadNCMetaBlobAsync(release.Digest);
            if (bytes == null || bytes.Length > 1024 * 1024) return Failure("storage");
            var verified = await Task.Run(() => { var module = MetaBytecodeLoader.Load(bytes); return MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(module)); });
            if (verified.ContentHash != release.Digest || !MetaSoftwareVersion.TryParse(verified.Manifest.PackageVersion, out var version)) return Failure("input");
            var id = draft.ProductId == Guid.Empty ? NCMetaPersistenceSystem.StableGuid("software-product:" + publisher + ":" + requestId) : draft.ProductId;
            if (release.CommercialOrigin is { } origin && (origin.ProductId != id || origin.Major != version.Major || origin.PublisherProjectId != projectId)) return Failure("access");
            var identity = await Read<NCMetaSoftwareProductIdentity>("meta.software.identity", id.ToString("D"));
            if (identity != null && (identity.Publisher != publisher || identity.ProjectId != projectId)) return Failure("access");
            foreach (var dependency in release.Dependencies ?? [])
            {
                var permitted = await ResolveRelease(publisher, context.DeviceId, dependency.ProductId, dependency.ReleaseId, forBuild: true);
                var dependencyProduct = permitted == null ? null : await ReadProductAsync(dependency.ProductId, MetaSoftwareVersion.TryParse(permitted.Descriptor.Version, out var parsed) ? parsed.Major : 0);
                if (permitted == null || dependencyProduct == null || dependency.BlobDigest != permitted.Release.Digest || dependency.ArtifactDigest != permitted.Descriptor.ContainerDigest || dependency.PackageIdentity != permitted.Descriptor.PackageIdentity || dependency.Version != permitted.Descriptor.Version ||
                    dependencyProduct.Publisher != publisher && !dependencyProduct.Terms.AllowRedistribution) return Failure("access");
            }
            var key = Key(id, version.Major); var old = await ReadProductAsync(id, version.Major);
            if (old != null && (old.Publisher != publisher || old.ProjectId != projectId || MetaSoftwarePolicy.TermsFingerprint(old.Terms) != MetaSoftwarePolicy.TermsFingerprint(draft.Terms))) return Failure("access");
            if (old?.Releases.Any(known => known.Id == releaseId || known.Version == version.ToString()) == true) return old.Releases.Any(known => known.Id == releaseId && known.ArtifactDigest == release.Digest) ? new(true, "nc-meta-software-published", Product: old) : Failure("conflict");
            if (old?.Releases.Length >= 64) return Failure("quota");
            var interfaces = verified.GetLinks().Select(link => verified.GetType(link.CapabilityTypeId).Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var descriptor = new MetaSoftwareRelease(release.Id, release.Digest, verified.Manifest.PackageName, version.ToString(), verified.Manifest.RuntimeAbi, verified.Manifest.ApiCatalogHash, verified.Manifest.RequiredScopes, interfaces, verified.ResidentMemoryBytes, context.Time.Ticks, draft.Changelog)
            { ContainerDigest = verified.Digest, MinimumProcessMemoryBytes = verified.MinimumProcessMemoryBytes, MaximumConstantStringBytes = verified.MaximumConstantStringBytes,
                PackageBytes = checked(bytes.Length + (release.Resources?.Sum(value => (long) value.Bytes) ?? 0)) };
            if (old != null && old.Releases.Any(known => known.PackageIdentity != descriptor.PackageIdentity)) return Failure("input");
            var product = new MetaSoftwareProduct(id, publisher, projectId, draft.Name, draft.Description, version.Major, draft.Terms, draft.Price, [.. old?.Releases ?? [], descriptor], true, checked((old?.Revision ?? 0) + 1));
            var catalog = await Read<NCMetaSoftwareCatalog>("meta.software.catalog", "public") ?? new([], 0);
            var entries = catalog.Products.Where(entry => entry.ProductId != id || entry.Major != version.Major).Append(new NCMetaSoftwareCatalogEntry(id, version.Major)).ToArray();
            if (entries.Length > 256) return Failure("quota");
            if (!await CanActFor(context, publisher)) return Failure("access");
            var fingerprint = MetaSoftwarePolicy.Fingerprint(context.Actor, publisher, projectId.ToString("D"), releaseId.ToString("D"), release.Digest, JsonSerializer.Serialize(draft, Json));
            var writes = new List<NCAtomicLedgerParticipantWrite> { Write(ProductType, key, old?.Revision ?? 0, product), Write(PublicationType, key + ":" + releaseId.ToString("D"), 0, new NCMetaSoftwarePublication(id, version.Major, release, 1) { RedistributionApproved = true }), Write("meta.software.release-index", id.ToString("D") + ":" + releaseId.ToString("D"), 0, new NCMetaSoftwareReleaseLocator(id, releaseId, version.Major, 1)), Write("meta.software.catalog", "public", catalog.Revision, new NCMetaSoftwareCatalog(entries, catalog.Revision + 1)) };
            writes.Add(Write("meta.software.publisher-image", projectId.ToString("D") + ":" + releaseId.ToString("D"), 0,
                new NCMetaSoftwarePublisherImage(projectId, releaseId, id, version.Major, release.Digest, 1)));
            if (identity == null) writes.Add(Write("meta.software.identity", id.ToString("D"), 0, new NCMetaSoftwareProductIdentity(id, publisher, projectId, 1)));
            return await Batch(context, requestId, "publish", fingerprint, writes) ? new(true, "nc-meta-software-published", Product: product) : Failure("conflict");
        }
        catch (MetaFormatException) { return Failure("invalid-artifact"); }
        finally { _publicationGate.Release(); }
    }
    private async Task<T?> Read<T>(string type, string id) where T : class
    {
        var snapshot = await _snapshots.ReadCurrentAsync(type, id);
        if (snapshot == null || snapshot.AggregateType != type || snapshot.AggregateId != id || snapshot.FormatVersion != 1 || snapshot.Payload.Length > 512 * 1024 || Encoding.UTF8.GetByteCount(snapshot.Payload) > 512 * 1024) return null;
        try { var value = JsonSerializer.Deserialize<T>(snapshot.Payload, Json); return value != null && ValidSnapshot(value, type, id, snapshot.AggregateVersion) ? value : null; }
        catch (JsonException) { return null; }
    }
    private async Task<bool> Batch(Context context, Guid request, string operation, string fingerprint, IReadOnlyList<NCAtomicLedgerParticipantWrite> writes,
        string? acknowledgement = null)
    {
        var authority = await CaptureMutationAuthority(context);
        if (authority == null) return false;
        await BeforeAuthorityCommitAsync(request);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(NCPersistentIdentity.Command("meta", "software-" + operation, context.Actor + ":" + request), "meta.software." + operation, "meta.owner", context.Actor,
            writes.Where(write => write.AggregateType != CommandType).Select(write => new NCPersistentAggregateWrite(write.EventId, write.EventType, write.AggregateType, write.AggregateId, write.ExpectedAggregateVersion, write.SnapshotFormatVersion, write.SnapshotPayload, write.EventPayload)).ToArray(),
            acknowledgement ?? (writes.Any(write => write.AggregateType == CommandType) ? Acknowledgement(context, operation, fingerprint, writes) : "{}"),
            request, null, context.Epoch, context.Time, [], RequestFingerprint: fingerprint, Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { completion.SetResult(action()); } catch (Exception exception) { completion.SetException(exception); } });
        return completion.Task;
    }
    private static NCAtomicLedgerParticipantWrite Write<T>(string type, string id, long revision, T value) => new(Guid.NewGuid(), "meta.software.changed", type, id, revision, 1, JsonSerializer.Serialize(value, Json), JsonSerializer.Serialize(new { Type = type, Id = id, PreviousRevision = revision }, Json));
    private static string Key(Guid productId, int major) => productId.ToString("D") + ":" + major.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static NCMetaSoftwareOutcome Failure(string reason) => new(false, "nc-meta-software-" + reason);
}
