// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Contracts;

public sealed partial class NCMetaDevelopmentContractSystem
{
    private async Task<NCMetaDevelopmentOutcome> Offer(NativeContext context, Guid requestId,
        NCMetaDevelopmentRequest request, string fingerprint)
    {
        var offer = request.Offer;
        if (offer == null || offer.Customer != context.Owner || offer.DeviceIds is not { Count: >= 1 and <= 3 } ||
            offer.Subject is not { Length: >= 1 and <= 64 } || string.IsNullOrWhiteSpace(offer.Subject) || offer.Subject.Any(char.IsControl) ||
            offer.FixtureFiles is not { Count: >= 1 and <= 16 } || offer.MaximumScopes is not { Count: <= 64 } ||
            offer.DeadlineMinutes is < 1 or > 43200 || offer.SupportMinutes is < 0 or > 525600 ||
            !NCPartyRef.TryParse(offer.Developer, out var developer) || !NCPartyRef.TryParse(offer.Customer, out var customer) ||
            !(await _parties.ResolveAsync(developer, true)).IsResolved || !(await _parties.ResolveAsync(customer, true)).IsResolved ||
            offer.Arbitrator != null && (!NCPartyRef.TryParse(offer.Arbitrator, out var arbiter) ||
                !(await _parties.ResolveAsync(arbiter, true)).IsResolved)) return Fail("input");
        var project = await _persistence.ReadProjectAsync(request.ProjectId, context.Owner);
        if (project == null || offer.FixtureFiles.Distinct().Count() != offer.FixtureFiles.Count ||
            offer.DeviceIds.Distinct(StringComparer.Ordinal).Count() != offer.DeviceIds.Count) return Fail("input");
        var fixtures = new List<MetaDevelopmentFixture>();
        foreach (var id in offer.FixtureFiles)
        {
            var file = project.Files.SingleOrDefault(value => value.Id == id);
            if (file == null || !file.Path.EndsWith(".test.json", StringComparison.Ordinal) || file.Bytes > 65536) return Fail("fixture");
            var bytes = await _database.ReadNCMetaBlobAsync(file.Digest);
            if (bytes == null || bytes.Length != file.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Digest) return Fail("storage");
            var fixture = MetaTestHost.ParseFixture(Utf8.GetString(bytes));
            if (fixture.ExpectedCommands == null && fixture.ExpectedLogs == null) return Fail("fixture");
            fixtures.Add(new(file.Path, file.Digest, file.Bytes));
        }
        var targets = await Main(() =>
        {
            if (!Current(context)) return null;
            var result = new List<MetaDevelopmentTarget>();
            foreach (var id in offer.DeviceIds)
            {
                if (!_devices.TryResolve(id, out var entry) || !entry.Ready || entry.Owner != context.Owner ||
                    !TryComp<NCMetaHostComponent>(entry.Entity, out var host)) return null;
                result.Add(new(id, host.Profile.Id, "User", entry.AuthorityEpoch));
            }
            return result.ToArray();
        });
        if (targets == null) return Fail("access");
        var terms = new MetaDevelopmentTerms(offer.Customer, offer.Developer, offer.Subject, offer.Requirements,
            offer.FailureConditions, offer.Price, checked(context.Time.Ticks + offer.DeadlineMinutes * TimeSpan.TicksPerMinute),
            offer.AutomaticAcceptance ? MetaDevelopmentAcceptance.FixturesAutomatic : MetaDevelopmentAcceptance.FixturesAndCustomer,
            offer.SourceCopy ? MetaDevelopmentSource.SourceCopyAndBuild : MetaDevelopmentSource.BinaryLicense,
            offer.SupportMinutes * TimeSpan.TicksPerMinute, offer.Arbitrator, targets, offer.MaximumScopes.ToArray(),
            offer.ResidentBytes, offer.ProcessBytes, offer.PackageBytes, fixtures.ToArray(), MetaCompiler.BuildId,
            MetaApiCatalog.Default.Hash, MetaDevelopmentPolicy.RunnerPolicy);
        if (!MetaDevelopmentPolicy.Valid(terms, context.Time.Ticks)) return Fail("input");
        var contract = new MetaDevelopmentContract(CommandId(context.Actor, requestId), 1, terms,
            MetaDevelopmentPolicy.Hash(terms), MetaDevelopmentPhase.Proposed, []);
        var writes = new List<Content.Server.Database.NCPersistentAggregateWrite> { Write(ContractType, contract.Id.ToString("D"), 0, contract) };
        var catalog = await Read<NCMetaDevelopmentCatalog>(CatalogType, "active", value => value.Revision) ?? new([], 0);
        if (catalog.Contracts.Length >= 1024) return Fail("quota");
        writes.Add(Write(CatalogType, "active", catalog.Revision,
            catalog with { Contracts = catalog.Contracts.Append(contract.Id).ToArray(), Revision = catalog.Revision + 1 }));
        foreach (var party in new[] { terms.Customer, terms.Developer, terms.Arbitrator }.Where(value => value != null).Cast<string>().Distinct(StringComparer.Ordinal))
        {
            var old = await Read<NCMetaDevelopmentPartyIndex>(IndexType, party, value => value.Revision) ?? new(party, [], 0);
            if (old.Party != party || old.Contracts.Length > 128) return Fail("storage");
            var retained = old.Contracts.ToList();
            // Keep active/disputed work discoverable; trim only terminal history at the bounded page limit.
            if (retained.Count == 128)
            {
                foreach (var id in old.Contracts)
                {
                    var previous = await ReadContractAsync(id);
                    if (previous?.Phase is not (MetaDevelopmentPhase.Settled or MetaDevelopmentPhase.Refunded or MetaDevelopmentPhase.Cancelled)) continue;
                    retained.Remove(id);
                    break;
                }
            }
            if (retained.Count >= 128) return Fail("quota");
            writes.Add(Write(IndexType, party, old.Revision, old with { Contracts = retained.Append(contract.Id).ToArray(), Revision = old.Revision + 1 }));
        }
        return await Commit(context, requestId, fingerprint, contract, writes, "offered");
    }

    private async Task<NCMetaDevelopmentOutcome> Change(NativeContext context, Guid requestId,
        NCMetaDevelopmentRequest request, MetaDevelopmentContract current, string fingerprint)
    {
        var customerOrDeveloper = context.Owner == current.Terms.Customer || context.Owner == current.Terms.Developer;
        MetaDevelopmentContract next;
        if (request.Action == NCMetaDevelopmentAction.Consent && customerOrDeveloper && request.Confirm)
        {
            if (!MetaDevelopmentPolicy.TryConsent(current, new(context.Owner, context.Actor, current.TermsHash,
                request.AutomaticConsent, CommandId(context.Actor, requestId), context.Time.Ticks), out next!)) return Fail("action");
        }
        else if (request.Action == NCMetaDevelopmentAction.Withdraw && customerOrDeveloper && current.Phase == MetaDevelopmentPhase.Proposed)
            next = current with { Phase = MetaDevelopmentPhase.Cancelled, Revision = current.Revision + 1 };
        else if (request.Action == NCMetaDevelopmentAction.Dispute && customerOrDeveloper && request.Text.Length is > 0 and <= 1024 &&
                 !string.IsNullOrWhiteSpace(request.Text) && MetaDevelopmentPolicy.TryDispute(current, out var disputed))
            next = disputed with { DisputeReason = request.Text };
        else if (request.Action == NCMetaDevelopmentAction.ProposeResolution && customerOrDeveloper && current.Phase == MetaDevelopmentPhase.Disputed)
        {
            if (request.CustomerAmount < 0 || request.DeveloperAmount < 0 ||
                (decimal) request.CustomerAmount + request.DeveloperAmount != current.Terms.Price || request.TransferDelivery && current.Delivery == null) return Fail("input");
            var resolution = new MetaDevelopmentResolution("", current.Revision, request.CustomerAmount, request.DeveloperAmount, request.TransferDelivery, []);
            resolution = resolution with { Hash = MetaDevelopmentPolicy.ResolutionHash(current, resolution) };
            next = current with { Resolution = resolution, Revision = current.Revision + 1 };
        }
        else if (request.Action == NCMetaDevelopmentAction.ConsentResolution && customerOrDeveloper && request.Confirm &&
                 current.Phase == MetaDevelopmentPhase.Disputed && current.Resolution is { } resolution && request.ResolutionHash == resolution.Hash)
        {
            resolution = resolution with { Consents = resolution.Consents.Where(consent => consent.Party != context.Owner)
                .Append(new(context.Owner, context.Actor, resolution.Hash, false, CommandId(context.Actor, requestId), context.Time.Ticks)).ToArray() };
            next = current with { Resolution = resolution, Revision = current.Revision + 1 };
            if (MetaDevelopmentPolicy.ValidDistribution(next, resolution))
                return await Pay(context, requestId, fingerprint, next, resolution.CustomerAmount, resolution.DeveloperAmount,
                    resolution.TransferDelivery, false, expected: current);
        }
        else if (request.Action == NCMetaDevelopmentAction.Arbitrate && request.Confirm && context.Owner == current.Terms.Arbitrator &&
                 current.Phase == MetaDevelopmentPhase.Disputed && current.Resolution is { } adjudicated && request.ResolutionHash == adjudicated.Hash &&
                 MetaDevelopmentPolicy.ValidDistribution(current, adjudicated, nativeArbitratorAuthorized: true))
            return await Pay(context, requestId, fingerprint, current, adjudicated.CustomerAmount, adjudicated.DeveloperAmount, adjudicated.TransferDelivery, false);
        else return Fail("action");
        var writes = new List<Content.Server.Database.NCPersistentAggregateWrite> { Write(ContractType, current.Id.ToString("D"), current.Revision, next) };
        if (next.Phase == MetaDevelopmentPhase.Cancelled)
        {
            var removal = await PrepareCatalogRemoval(current.Id);
            if (removal == null) return Fail("storage");
            writes.Add(removal);
        }
        return await Commit(context, requestId, fingerprint, next, writes);
    }
}
