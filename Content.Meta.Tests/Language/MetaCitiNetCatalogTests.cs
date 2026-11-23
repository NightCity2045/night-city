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
}
