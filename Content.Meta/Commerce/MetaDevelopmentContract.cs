// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Hosting;
using Content.Meta.Language;

namespace Content.Meta.Commerce;

public enum MetaDevelopmentPhase { Proposed, Funded, Delivered, Disputed, Settled, Refunded, Cancelled }
public enum MetaDevelopmentAcceptance { FixturesAndCustomer, FixturesAutomatic }
public enum MetaDevelopmentSource { BinaryLicense, SourceCopyAndBuild }
public enum MetaDevelopmentTestStatus { Passed, Failed, Faulted, LimitExceeded, InvalidFixture, Unavailable }
public sealed record MetaDevelopmentTarget(string DeviceId, string Profile, string Slot, ulong AuthorityEpoch);
public sealed record MetaDevelopmentFixture(string Path, string Digest, int Bytes);
public sealed record MetaDevelopmentTerms(string Customer, string Developer, string Subject, string Requirements,
    string FailureConditions, long Price, long DeliveryDueTicks, MetaDevelopmentAcceptance Acceptance,
    MetaDevelopmentSource Source, long SupportTicks, string? Arbitrator, MetaDevelopmentTarget[] Targets,
    string[] MaximumScopes, long MaximumResidentBytes, long MaximumProcessBytes, long MaximumPackageBytes,
    MetaDevelopmentFixture[] Fixtures, string CompilerBuild, string CatalogHash, string RunnerPolicy);
public sealed record MetaDevelopmentConsent(string Party, string ActualActor, string TermsHash,
    bool ExplicitAutomatic, Guid NativeCommandId, long AcceptedAtTicks);
public sealed record MetaDevelopmentDelivery(Guid Id, Guid ContractId, long Revision, string TermsHash,
    Guid DeveloperProjectId, Guid ReleaseId, Guid ProductId, string ContainerDigest, string ContentHash,
    string SourceTreeHash, string ResourceTreeHash, string DependencyHash, string FixtureHash,
    string CompilerBuild, string CatalogHash, string RunnerPolicy, long SubmittedAtTicks);
public sealed record MetaDevelopmentReceipt(Guid Id, Guid ContractId, string TermsHash, Guid DeliveryId,
    long DeliveryRevision, string ContainerDigest, string ContentHash, string SourceTreeHash, string ResourceTreeHash, string DependencyHash, string FixtureHash,
    string CatalogHash, string CompilerBuild, string RunnerPolicy, MetaDevelopmentTestStatus Status,
    int FixtureCount, long GasUsed, int Slices, string TraceDigest, long CompletedAtTicks, bool Consumed = false);
public sealed record MetaDevelopmentResolution(string Hash, long BaseRevision, long CustomerAmount, long DeveloperAmount,
    bool TransferDelivery, MetaDevelopmentConsent[] Consents);
public sealed record MetaDevelopmentContract(Guid Id, long Revision, MetaDevelopmentTerms Terms, string TermsHash,
    MetaDevelopmentPhase Phase, MetaDevelopmentConsent[] Consents, Guid FundingTransactionId = default,
    MetaDevelopmentDelivery? Delivery = null, Guid AcceptanceReceiptId = default,
    MetaDevelopmentResolution? Resolution = null, Guid OutcomeTransactionId = default, string DisputeReason = "");

/// <summary>Pure transition checks. Native actor/receipt/payment authenticity belongs to the server boundary.</summary>
public static class MetaDevelopmentPolicy
{
    public const long MaximumPrice = 1_000_000_000;
    public const int MaximumFixtures = 16;
    public const int MaximumFixtureBytes = 64 * 1024;
    public const string RunnerPolicy = "META-IsolatedFixtures/1";
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Bounded(string? text, int bytes) => text != null && Encoding.UTF8.GetByteCount(text) <= bytes;
    public static bool Valid(MetaDevelopmentTerms terms, long now) => now >= 0 &&
        terms.Customer != terms.Developer && Bounded(terms.Customer, 128) && terms.Customer.Length > 0 &&
        Bounded(terms.Developer, 128) && terms.Developer.Length > 0 && Bounded(terms.Subject, 128) && terms.Subject.Length > 0 &&
        Bounded(terms.Requirements, 4096) && Bounded(terms.FailureConditions, 2048) &&
        terms.Price is > 0 and <= MaximumPrice && terms.DeliveryDueTicks > now &&
        terms.DeliveryDueTicks - now <= 30 * TimeSpan.TicksPerDay &&
        Enum.IsDefined(terms.Acceptance) && Enum.IsDefined(terms.Source) &&
        terms.SupportTicks is >= 0 and <= 365 * TimeSpan.TicksPerDay &&
        terms.Targets is { Length: >= 1 and <= 4 } && terms.Targets.All(target => target != null &&
            MetaSoftwarePolicy.Device(target.DeviceId) && target.AuthorityEpoch >= 2 && Bounded(target.Profile, 128) && target.Profile.Length > 0 &&
            target.Slot is "User" or "Firmware") && terms.Targets.Select(target => target.DeviceId).Distinct(StringComparer.Ordinal).Count() == terms.Targets.Length &&
        terms.MaximumScopes is { Length: <= 64 } && terms.MaximumScopes.All(scope => Bounded(scope, 128) && scope.Length > 0) &&
        terms.MaximumScopes.Distinct(StringComparer.Ordinal).Count() == terms.MaximumScopes.Length &&
        terms.MaximumResidentBytes is >= 0 and <= 16 * 1024 * 1024 && terms.MaximumProcessBytes is >= 4096 and <= 1024 * 1024 &&
        terms.MaximumPackageBytes is > 0 and <= 8 * 1024 * 1024 && terms.CatalogHash == MetaApiCatalog.Default.Hash && terms.RunnerPolicy == RunnerPolicy &&
        terms.CompilerBuild == MetaCompiler.BuildId && (terms.Arbitrator == null || Bounded(terms.Arbitrator, 128) && terms.Arbitrator.Length > 0) &&
        terms.Fixtures is { Length: >= 1 and <= MaximumFixtures } && terms.Fixtures.All(fixture => fixture != null &&
            Bounded(fixture.Path, 256) && MetaPaths.IsValid(fixture.Path) && Digest(fixture.Digest) && fixture.Bytes is > 0 and <= MaximumFixtureBytes) &&
        terms.Fixtures.Sum(fixture => (long) fixture.Bytes) <= MaximumFixtureBytes &&
        terms.Fixtures.Select(fixture => fixture.Path).Distinct(StringComparer.Ordinal).Count() == terms.Fixtures.Length;

    public static string Hash(MetaDevelopmentTerms terms)
    {
        var canonical = terms with { Targets = terms.Targets.OrderBy(target => target.DeviceId, StringComparer.Ordinal).ToArray(),
            MaximumScopes = terms.MaximumScopes.Order(StringComparer.Ordinal).ToArray(),
            Fixtures = terms.Fixtures.OrderBy(fixture => fixture.Path, StringComparer.Ordinal).ToArray() };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Format = 1, Terms = canonical }))));
    }

    public static bool BothConsented(MetaDevelopmentContract contract) =>
        new[] { contract.Terms.Customer, contract.Terms.Developer }.All(party => contract.Consents.Any(consent =>
            consent.Party == party && consent.TermsHash == contract.TermsHash && consent.NativeCommandId != Guid.Empty &&
            (contract.Terms.Acceptance != MetaDevelopmentAcceptance.FixturesAutomatic || consent.ExplicitAutomatic)));

    public static bool TryConsent(MetaDevelopmentContract current, MetaDevelopmentConsent consent, out MetaDevelopmentContract next)
    {
        next = current;
        if (current.Phase != MetaDevelopmentPhase.Proposed || consent.TermsHash != current.TermsHash ||
            consent.Party != current.Terms.Customer && consent.Party != current.Terms.Developer ||
            consent.NativeCommandId == Guid.Empty || consent.AcceptedAtTicks < 0 || consent.AcceptedAtTicks >= current.Terms.DeliveryDueTicks ||
            !Bounded(consent.ActualActor, 128) || consent.ActualActor.Length == 0 ||
            current.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic && !consent.ExplicitAutomatic) return false;
        next = current with { Revision = checked(current.Revision + 1),
            Consents = current.Consents.Where(value => value.Party != consent.Party).Append(consent).ToArray() };
        return true;
    }

    public static bool TryFund(MetaDevelopmentContract current, Guid transaction, long now, out MetaDevelopmentContract next)
    {
        next = current;
        if (transaction == Guid.Empty || current.Phase != MetaDevelopmentPhase.Proposed || !BothConsented(current) ||
            !Valid(current.Terms, now) || Hash(current.Terms) != current.TermsHash) return false;
        next = current with { Revision = checked(current.Revision + 1), Phase = MetaDevelopmentPhase.Funded, FundingTransactionId = transaction };
        return true;
    }

    public static bool TryDeliver(MetaDevelopmentContract current, MetaDevelopmentDelivery delivery, out MetaDevelopmentContract next)
    {
        next = current;
        if (current.Phase is not (MetaDevelopmentPhase.Funded or MetaDevelopmentPhase.Delivered) ||
            delivery.ContractId != current.Id || delivery.TermsHash != current.TermsHash || delivery.Id == Guid.Empty ||
            delivery.Revision != (current.Delivery?.Revision ?? 0) + 1 || delivery.SubmittedAtTicks < 0 || delivery.SubmittedAtTicks > current.Terms.DeliveryDueTicks ||
            delivery.CatalogHash != current.Terms.CatalogHash || delivery.RunnerPolicy != current.Terms.RunnerPolicy ||
            delivery.CompilerBuild != current.Terms.CompilerBuild ||
            !Digest(delivery.ContainerDigest) || !Digest(delivery.ContentHash) || !Digest(delivery.SourceTreeHash) ||
            !Digest(delivery.ResourceTreeHash) || !Digest(delivery.DependencyHash) || !Digest(delivery.FixtureHash)) return false;
        next = current with { Revision = checked(current.Revision + 1), Phase = MetaDevelopmentPhase.Delivered,
            Delivery = delivery, AcceptanceReceiptId = Guid.Empty };
        return true;
    }

    public static bool ReceiptMatches(MetaDevelopmentContract contract, MetaDevelopmentReceipt receipt) =>
        contract.Delivery is { } delivery && receipt.Id != Guid.Empty && receipt.ContractId == contract.Id &&
        receipt.TermsHash == contract.TermsHash && receipt.DeliveryId == delivery.Id && receipt.DeliveryRevision == delivery.Revision &&
        receipt.ContainerDigest == delivery.ContainerDigest && receipt.ContentHash == delivery.ContentHash &&
        receipt.SourceTreeHash == delivery.SourceTreeHash && receipt.ResourceTreeHash == delivery.ResourceTreeHash &&
        receipt.DependencyHash == delivery.DependencyHash && receipt.FixtureHash == delivery.FixtureHash &&
        receipt.CatalogHash == delivery.CatalogHash && receipt.CompilerBuild == delivery.CompilerBuild &&
        receipt.RunnerPolicy == delivery.RunnerPolicy && receipt.FixtureCount == contract.Terms.Fixtures.Length &&
        receipt.CompletedAtTicks >= delivery.SubmittedAtTicks && receipt.CompletedAtTicks <= contract.Terms.DeliveryDueTicks &&
        receipt.Status == MetaDevelopmentTestStatus.Passed && !receipt.Consumed;

    public static bool CanSettle(MetaDevelopmentContract contract, MetaDevelopmentReceipt trustedReceipt,
        bool freshNativeCustomerConfirmed) => contract.Phase == MetaDevelopmentPhase.Delivered && Hash(contract.Terms) == contract.TermsHash && BothConsented(contract) &&
        ReceiptMatches(contract, trustedReceipt) &&
        (contract.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic || freshNativeCustomerConfirmed);

    public static bool CanRefundUndelivered(MetaDevelopmentContract contract, long now) =>
        contract.Phase == MetaDevelopmentPhase.Funded && contract.Delivery == null && now >= contract.Terms.DeliveryDueTicks;

    public static bool TryDispute(MetaDevelopmentContract current, out MetaDevelopmentContract next)
    {
        next = current;
        if (current.Phase is not (MetaDevelopmentPhase.Funded or MetaDevelopmentPhase.Delivered)) return false;
        next = current with { Revision = checked(current.Revision + 1), Phase = MetaDevelopmentPhase.Disputed };
        return true;
    }
    public static string ResolutionHash(MetaDevelopmentContract contract, MetaDevelopmentResolution resolution) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { Format = 1, ContractId = contract.Id, contract.TermsHash, DeliveryId = contract.Delivery?.Id,
            resolution.BaseRevision, resolution.CustomerAmount, resolution.DeveloperAmount, resolution.TransferDelivery }))));

    public static bool ValidDistribution(MetaDevelopmentContract contract, MetaDevelopmentResolution resolution, bool nativeArbitratorAuthorized = false) =>
        contract.Phase == MetaDevelopmentPhase.Disputed && resolution.BaseRevision > 0 && resolution.BaseRevision <= contract.Revision &&
        ResolutionHash(contract, resolution) == resolution.Hash && resolution.CustomerAmount >= 0 && resolution.DeveloperAmount >= 0 &&
        (decimal) resolution.CustomerAmount + resolution.DeveloperAmount == contract.Terms.Price &&
        (!resolution.TransferDelivery || contract.Delivery != null) &&
        (nativeArbitratorAuthorized || new[] { contract.Terms.Customer, contract.Terms.Developer }.All(party => resolution.Consents.Any(consent =>
            consent.Party == party && consent.TermsHash == resolution.Hash && consent.NativeCommandId != Guid.Empty)));
}
