// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaCitiNetCatalogTests
{
    [Test]
    public void TheCatalogBeforeCitiNetStaysTrustedAndLacksTheCapabilities()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreCitiNetCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreCitiNetCatalogHash));
        Assert.That(MetaApiCatalog.Default.Hash, Is.Not.EqualTo(MetaApiCatalog.PreCitiNetCatalogHash));
        Assert.That(old.Members.Any(member => member.Owner.StartsWith("meta.citinet.", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public void TheBrowserOpensAnAddressAndTheGatewayNamesTheVisitorDevice()
    {
        var members = MetaApiCatalog.Default.Members.ToDictionary(member => member.QualifiedName);
        Assert.That(members["meta.citinet.Browser.open"].Scope, Is.EqualTo("citinet.browse"));
        Assert.That(members["meta.citinet.Gateway.visitorDevice"].Scope, Is.EqualTo("citinet.visitor"));
    }

    [Test]
    public void TheCatalogBeforeTheWalletStaysTrustedAndLacksIt()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreWalletCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreWalletCatalogHash));
        Assert.That(old.Members.Any(member => member.Owner == "meta.economy.Wallet"), Is.False);
        var pay = MetaApiCatalog.Default.Members.Single(member => member.QualifiedName == "meta.economy.Wallet.pay");
        Assert.That(pay.Scope, Is.EqualTo("economy.pay"));
    }

    [Test]
    public void TheCatalogBeforeTheCountermeasureStaysTrustedAndLacksIt()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreCountermeasureCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreCountermeasureCatalogHash));
        Assert.That(old.Members.Any(member => member.Owner == "meta.security.Countermeasure"), Is.False);
        var surge = MetaApiCatalog.Default.Members.Single(member => member.QualifiedName == "meta.security.Countermeasure.surge");
        Assert.That(surge.Scope, Is.EqualTo("security.countermeasure"));
    }

    [Test]
    public void TheCatalogBeforeProgramOffersStaysTrustedAndLacksThem()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreOfferCatalogHash);
        Assert.That(old.Hash, Is.EqualTo(MetaApiCatalog.PreOfferCatalogHash));
        Assert.That(old.Members.Any(member => member.QualifiedName == "meta.citinet.Gateway.offer"), Is.False);
        var accept = MetaApiCatalog.Default.Members.Single(member => member.QualifiedName == "meta.citinet.Browser.accept");
        Assert.That(accept.Scope, Is.EqualTo("citinet.install"));
    }
}
