// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Commerce;

[TestFixture]
public sealed class MetaDevelopmentContractTests
{
    private static string Hash(char c) => new(c, 64);
    private static MetaDevelopmentContract Contract(MetaDevelopmentAcceptance mode = MetaDevelopmentAcceptance.FixturesAndCustomer)
    {
        var terms = new MetaDevelopmentTerms("character:1", "character:2", "Alarm", "Detect visible motion", "Camera loss must not hang",
            100, 10 * TimeSpan.TicksPerMinute, mode, MetaDevelopmentSource.SourceCopyAndBuild, 0, null,
            [new(Guid.NewGuid().ToString("D"), "NCMetaNetworkHost", "User", 2)], [], 1024 * 1024, 1024 * 1024,
            1024 * 1024, [new("alarm.test.json", Hash('a'), 100)], MetaCompiler.BuildId, MetaApiCatalog.Default.Hash, MetaDevelopmentPolicy.RunnerPolicy);
        var hash = MetaDevelopmentPolicy.Hash(terms);
        return new(Guid.NewGuid(), 1, terms, hash, MetaDevelopmentPhase.Proposed, []);
    }
    private static MetaDevelopmentContract Consented(MetaDevelopmentContract contract)
    {
        foreach (var party in new[] { contract.Terms.Customer, contract.Terms.Developer })
        {
            Assert.That(MetaDevelopmentPolicy.TryConsent(contract, new(party, party, contract.TermsHash,
                contract.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic, Guid.NewGuid(), 0), out var next), Is.True);
            contract = next;
        }
        return contract;
    }
    private static (MetaDevelopmentContract Contract, MetaDevelopmentReceipt Receipt) Delivered(MetaDevelopmentAcceptance mode)
    {
        var contract = Consented(Contract(mode));
        Assert.That(MetaDevelopmentPolicy.TryFund(contract, Guid.NewGuid(), 0, out var funded), Is.True);
        var delivery = new MetaDevelopmentDelivery(Guid.NewGuid(), contract.Id, 1, contract.TermsHash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Hash('b'), Hash('c'), Hash('d'), Hash('e'), Hash('f'), Hash('a'), MetaCompiler.BuildId,
            contract.Terms.CatalogHash, MetaDevelopmentPolicy.RunnerPolicy, 1);
        Assert.That(MetaDevelopmentPolicy.TryDeliver(funded, delivery, out var delivered), Is.True);
        var receipt = new MetaDevelopmentReceipt(Guid.NewGuid(), contract.Id, contract.TermsHash, delivery.Id, 1,
            delivery.ContainerDigest, delivery.ContentHash, delivery.SourceTreeHash, delivery.ResourceTreeHash, delivery.DependencyHash, delivery.FixtureHash,
            delivery.CatalogHash, delivery.CompilerBuild, delivery.RunnerPolicy, MetaDevelopmentTestStatus.Passed, 1, 100, 2, Hash('a'), 2);
        return (delivered, receipt);
    }

    [Test]
    public void AutomaticReleaseRequiresTwoExplicitConsentsToTheExactTerms()
    {
        var contract = Contract(MetaDevelopmentAcceptance.FixturesAutomatic);
        Assert.That(MetaDevelopmentPolicy.TryConsent(contract, new(contract.Terms.Customer, contract.Terms.Customer,
            contract.TermsHash, false, Guid.NewGuid(), 0), out _), Is.False);
        Assert.That(MetaDevelopmentPolicy.TryConsent(contract, new(contract.Terms.Customer, contract.Terms.Customer,
            contract.TermsHash, true, Guid.NewGuid(), 0), out var one), Is.True);
        Assert.That(MetaDevelopmentPolicy.TryFund(one, Guid.NewGuid(), 0, out _), Is.False);
        var both = Consented(contract);
        Assert.That(MetaDevelopmentPolicy.TryFund(both, Guid.NewGuid(), 0, out _), Is.True);
        var changed = both with { TermsHash = Hash('b') };
        Assert.That(MetaDevelopmentPolicy.TryFund(changed, Guid.NewGuid(), 0, out _), Is.False);
    }
    [Test]
    public void TechnicalPassDoesNotAcceptRpWorkOrReleaseDefaultEscrow()
    {
        var (contract, receipt) = Delivered(MetaDevelopmentAcceptance.FixturesAndCustomer);
        Assert.That(MetaDevelopmentPolicy.CanSettle(contract, receipt, false), Is.False);
        Assert.That(MetaDevelopmentPolicy.CanSettle(contract, receipt, true), Is.True);
        Assert.That(MetaDevelopmentPolicy.TryDispute(contract, out var disputed), Is.True);
        Assert.That(MetaDevelopmentPolicy.CanSettle(disputed, receipt, true), Is.False);
        Assert.That(MetaDevelopmentPolicy.CanRefundUndelivered(contract, contract.Terms.DeliveryDueTicks), Is.False);
    }
    [TestCase("delivery")][TestCase("revision")][TestCase("source")][TestCase("fixtures")]
    [TestCase("artifact")][TestCase("catalog")][TestCase("compiler")][TestCase("runner")]
    [TestCase("resources")][TestCase("dependencies")]
    [TestCase("deadline")][TestCase("consumed")][TestCase("unavailable")][TestCase("terms")]
    public void EarlierOrDifferentReceiptCannotAuthorizeCurrentSettlement(string changed)
    {
        var (contract, receipt) = Delivered(MetaDevelopmentAcceptance.FixturesAutomatic);
        var stale = changed switch
        {
            "delivery" => receipt with { DeliveryId = Guid.NewGuid() },
            "revision" => receipt with { DeliveryRevision = 2 },
            "source" => receipt with { SourceTreeHash = Hash('e') },
            "fixtures" => receipt with { FixtureHash = Hash('b') },
            "artifact" => receipt with { ContentHash = Hash('b') },
            "resources" => receipt with { ResourceTreeHash = Hash('b') },
            "dependencies" => receipt with { DependencyHash = Hash('b') },
            "catalog" => receipt with { CatalogHash = Hash('c') },
            "compiler" => receipt with { CompilerBuild = "other" },
            "runner" => receipt with { RunnerPolicy = "other" },
            "deadline" => receipt with { CompletedAtTicks = contract.Terms.DeliveryDueTicks + 1 },
            "consumed" => receipt with { Consumed = true },
            "unavailable" => receipt with { Status = MetaDevelopmentTestStatus.Unavailable },
            _ => receipt with { TermsHash = Hash('f') },
        };
        Assert.That(MetaDevelopmentPolicy.CanSettle(contract, stale, false), Is.False);
    }
    [Test]
    public void ExactDeadlineAndNoDeliveryGovernAutomaticRefund()
    {
        var contract = Consented(Contract());
        Assert.That(MetaDevelopmentPolicy.TryFund(contract, Guid.NewGuid(), 0, out var funded), Is.True);
        Assert.That(MetaDevelopmentPolicy.CanRefundUndelivered(funded, funded.Terms.DeliveryDueTicks - 1), Is.False);
        Assert.That(MetaDevelopmentPolicy.CanRefundUndelivered(funded, funded.Terms.DeliveryDueTicks), Is.True);
        Assert.That(MetaDevelopmentPolicy.CanRefundUndelivered(funded with { Phase = MetaDevelopmentPhase.Disputed }, funded.Terms.DeliveryDueTicks), Is.False);
    }
    [Test]
    public void SplitAmountsAndSourceRightsAreBoundByTheSameSignedResolution()
    {
        var (contract, _) = Delivered(MetaDevelopmentAcceptance.FixturesAndCustomer);
        Assert.That(MetaDevelopmentPolicy.TryDispute(contract, out var disputed), Is.True);
        var resolution = new MetaDevelopmentResolution("", disputed.Revision, 40, 60, true, []);
        resolution = resolution with { Hash = MetaDevelopmentPolicy.ResolutionHash(disputed, resolution) };
        resolution = resolution with { Consents = new[] { disputed.Terms.Customer, disputed.Terms.Developer }.Select(party =>
            new MetaDevelopmentConsent(party, party, resolution.Hash, false, Guid.NewGuid(), 3)).ToArray() };
        Assert.That(MetaDevelopmentPolicy.ValidDistribution(disputed, resolution), Is.True);
        Assert.That(MetaDevelopmentPolicy.ValidDistribution(disputed, resolution with { CustomerAmount = 60, DeveloperAmount = 40 }), Is.False);
        Assert.That(MetaDevelopmentPolicy.ValidDistribution(disputed, resolution with { TransferDelivery = false }), Is.False);
        Assert.That(MetaDevelopmentPolicy.ValidDistribution(disputed, resolution with { DeveloperAmount = long.MaxValue }), Is.False);
    }
}
