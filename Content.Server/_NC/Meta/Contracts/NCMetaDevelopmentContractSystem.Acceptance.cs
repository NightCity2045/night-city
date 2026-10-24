// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Hosting;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Contracts;

public sealed partial class NCMetaDevelopmentContractSystem
{
    private readonly SemaphoreSlim _testSlots = new(2, 2);
    private int _testQueue;
    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(JsonSerializer.Serialize(value, Json))));

    private async Task<NCMetaDevelopmentOutcome> Deliver(NativeContext context, Guid requestId,
        NCMetaDevelopmentRequest request, MetaDevelopmentContract current, string fingerprint)
    {
        if (context.Owner != current.Terms.Developer || current.Phase is not (MetaDevelopmentPhase.Funded or MetaDevelopmentPhase.Delivered) ||
            context.Time.Ticks >= current.Terms.DeliveryDueTicks) return Fail("action");
        var project = await _persistence.ReadProjectAsync(request.ProjectId, context.Owner);
        var release = project?.Releases.SingleOrDefault(value => value.Id == request.ReleaseId);
        if (project == null || release?.Exports == null || release.CommercialOrigin != null ||
            release.BuildAuthorization is { } authorization && authorization.Owner != context.Owner) return Fail("access");
        var bytes = await _database.ReadNCMetaBlobAsync(release.Digest);
        if (bytes == null || bytes.Length > 1024 * 1024) return Fail("storage");
        var artifact = await Task.Run(() => MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(bytes))));
        if (!await Main(() => OperationalAllowed(artifact.Digest, artifact.ContentHash))) return Fail("test-unavailable");
        if (artifact.ContentHash != release.Digest || artifact.Digest != release.Exports.ArtifactDigest ||
            artifact.Manifest.ApiCatalogHash != current.Terms.CatalogHash || release.CompilerBuild != current.Terms.CompilerBuild ||
            artifact.ResidentMemoryBytes > current.Terms.MaximumResidentBytes || artifact.MinimumProcessMemoryBytes > current.Terms.MaximumProcessBytes ||
            artifact.Manifest.RequiredScopes.Except(current.Terms.MaximumScopes, StringComparer.Ordinal).Any() ||
            bytes.LongLength + (release.Resources ?? []).Sum(value => (long) value.Bytes) > current.Terms.MaximumPackageBytes)
            return Fail("outside-terms");
        var delivery = new MetaDevelopmentDelivery(NCMetaPersistenceSystem.StableGuid("development-delivery:" + CommandId(context.Actor, requestId)),
            current.Id, (current.Delivery?.Revision ?? 0) + 1, current.TermsHash, project.Id, release.Id,
            NCMetaPersistenceSystem.StableGuid("development-product:" + current.Id), artifact.Digest, artifact.ContentHash,
            release.Exports.SourceTreeHash, Digest((release.Resources ?? []).OrderBy(value => value.Path, StringComparer.Ordinal)),
            Digest(release.Dependencies ?? []), Digest(current.Terms.Fixtures.OrderBy(value => value.Path, StringComparer.Ordinal)),
            release.CompilerBuild, artifact.Manifest.ApiCatalogHash, current.Terms.RunnerPolicy, context.Time.Ticks);
        var record = new NCMetaDevelopmentDeliveryRecord(delivery, release, context.Owner,
            release.BuildAuthorization?.DeviceId ?? context.DeviceId, 1);
        // Freeze only exact released resources. Verify source headers/exports independently now;
        // native Test repeats verification before attestation, without trusting this metadata.
        var input = await LoadInput(current, record);
        if (input == null) return Fail("source-rights");
        var rebuilt = await Task.Run(() => new MetaCompiler().VerifyExports(input.AuthorizedSources, artifact));
        if (!await Main(() => OperationalAllowed(artifact.Digest, artifact.ContentHash))) return Fail("test-unavailable");
        if (rebuilt.SourceTreeHash != delivery.SourceTreeHash || !MetaDevelopmentPolicy.TryDeliver(current, delivery, out var next)) return Fail("source-rights");
        return await Commit(context, requestId, fingerprint, next,
            [Write(ContractType, current.Id.ToString("D"), current.Revision, next), Write(DeliveryType, delivery.Id.ToString("D"), 0, record)], "delivered");
    }

    private async Task<NCMetaDevelopmentFixtureInput?> LoadInput(MetaDevelopmentContract contract, NCMetaDevelopmentDeliveryRecord record)
    {
        var release = record.Release;
        var bytes = await _database.ReadNCMetaBlobAsync(release.Digest);
        if (bytes == null || bytes.Length > 1024 * 1024 || release.Digest != record.Delivery.ContentHash ||
            release.Exports == null || Digest((release.Resources ?? []).OrderBy(value => value.Path, StringComparer.Ordinal)) != record.Delivery.ResourceTreeHash ||
            Digest(release.Dependencies ?? []) != record.Delivery.DependencyHash) return null;
        var references = (release.Sources ?? []).Concat(release.Resources ?? []).ToArray();
        if (references.Length is < 1 or > 64 || references.Any(value => value.Bytes is < 0 or > 262144 || !MetaPaths.IsValid(value.Path)) ||
            references.Sum(value => (long) value.Bytes) > 1024 * 1024 || references.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() != references.Length) return null;
        var sources = new List<MetaSourceFile>();
        foreach (var file in references)
        {
            var content = await _database.ReadNCMetaBlobAsync(file.Digest);
            if (content == null || content.Length != file.Bytes || Convert.ToHexStringLower(SHA256.HashData(content)) != file.Digest) return null;
            if (file.Path.EndsWith(".meta", StringComparison.Ordinal))
            {
                if (file.FileId == Guid.Empty) return null;
                sources.Add(new(file.FileId.ToString("D"), Utf8.GetString(content)));
            }
        }
        if (sources.Count == 0) return null;
        var libraries = new List<MetaBinaryLibrary>();
        foreach (var dependency in release.Dependencies ?? [])
        {
            var resolved = await _marketplace.ResolveBuildDependencyAsync(record.Developer, record.BuildDeviceId, dependency.ProductId, dependency.ReleaseId);
            if (resolved == null || resolved.Value.Reference != dependency) return null;
            // Publication rights must be checked before any incoming customer source/bytecode claim.
            var product = await _marketplace.ReadProductAsync(dependency.ProductId,
                MetaSoftwareVersion.TryParse(dependency.Version, out var version) ? version.Major : 0);
            if (product == null || product.Publisher != record.Developer && !product.Terms.AllowRedistribution) return null;
            libraries.Add(resolved.Value.Library);
        }
        var fixtureBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var fixture in contract.Terms.Fixtures)
        {
            var content = await _database.ReadNCMetaBlobAsync(fixture.Digest);
            if (content == null || content.Length != fixture.Bytes || Convert.ToHexStringLower(SHA256.HashData(content)) != fixture.Digest) return null;
            fixtureBytes[fixture.Digest] = content;
        }
        var module = MetaBytecodeLoader.Load(bytes);
        return new(bytes, new(sources, module.Manifest.RootModule, module.Manifest.PackageName, module.Manifest.PackageVersion,
            BinaryDependencies: libraries), fixtureBytes, record.Delivery);
    }

    private async Task<NCMetaDevelopmentOutcome> Test(NativeContext context, Guid requestId,
        MetaDevelopmentContract current, string fingerprint)
    {
        if (current.Phase != MetaDevelopmentPhase.Delivered || current.Delivery == null ||
            context.Owner != current.Terms.Customer && context.Owner != current.Terms.Developer) return Fail("action");
        if (Interlocked.Increment(ref _testQueue) > 8) { Interlocked.Decrement(ref _testQueue); return Fail("busy"); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_testLifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var admitted = false;
        try
        {
            await _testSlots.WaitAsync(cancellation.Token); admitted = true;
            var delivery = await Read<NCMetaDevelopmentDeliveryRecord>(DeliveryType, current.Delivery.Id.ToString("D"), value => value.Revision);
            if (delivery == null || delivery.Delivery != current.Delivery && Digest(delivery.Delivery) != Digest(current.Delivery)) return Fail("storage");
            var input = await LoadInput(current, delivery);
            if (input == null) return Fail("source-rights");
            if (!await Main(() => OperationalAllowed(current.Delivery.ContainerDigest, current.Delivery.ContentHash))) return Fail("test-unavailable");
            var result = await Task.Run(() => NCMetaDevelopmentFixtureRunner.Run(current.Terms, input, cancellation.Token), cancellation.Token);
            var now = await Main(() => Current(context) && OperationalAllowed(current.Delivery.ContainerDigest, current.Delivery.ContentHash) &&
                _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1);
            if (now < 0) return Fail("access");
            var receipt = new MetaDevelopmentReceipt(NCMetaPersistenceSystem.StableGuid("development-acceptance:" + CommandId(context.Actor, requestId)),
                current.Id, current.TermsHash, current.Delivery.Id, current.Delivery.Revision, current.Delivery.ContainerDigest,
                current.Delivery.ContentHash, current.Delivery.SourceTreeHash, current.Delivery.ResourceTreeHash, current.Delivery.DependencyHash,
                current.Delivery.FixtureHash, current.Delivery.CatalogHash, current.Delivery.CompilerBuild, current.Delivery.RunnerPolicy,
                result.Status, result.Count, result.Gas, result.Slices, result.TraceDigest, now);
            var next = current with { Revision = current.Revision + 1, AcceptanceReceiptId = receipt.Id };
            var outcome = await Commit(context, requestId, fingerprint, next,
                [Write(ContractType, current.Id.ToString("D"), current.Revision, next),
                    Write(ReceiptType, receipt.Id.ToString("D"), 0, new NCMetaDevelopmentReceiptRecord(receipt, 1))],
                result.Status == MetaDevelopmentTestStatus.Passed ? "technical-passed" : "technical-failed");
            if (outcome.Success && next.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic &&
                MetaDevelopmentPolicy.CanSettle(next, receipt, false))
                return await Pay(null, NCMetaPersistenceSystem.StableGuid("development-auto:" + receipt.Id),
                    NCMetaPersistenceSystem.Fingerprint("automatic", next.Id.ToString("D"), receipt.Id.ToString("D")),
                    next, 0, next.Terms.Price, true, false, automaticEpoch: context.Epoch);
            return outcome;
        }
        catch (OperationCanceledException) { return Fail("test-unavailable"); }
        finally { if (admitted) _testSlots.Release(); Interlocked.Decrement(ref _testQueue); }
    }
}
