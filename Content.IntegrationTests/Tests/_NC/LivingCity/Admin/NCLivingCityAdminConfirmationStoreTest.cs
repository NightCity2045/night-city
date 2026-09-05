// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Admin;
using NUnit.Framework;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Admin;

[TestFixture]
public sealed class NCLivingCityAdminConfirmationStoreTest
{
    [Test]
    public void ConfirmationIsBoundToAdministratorAndExactRequestAndIsSingleUse()
    {
        var store = new NCLivingCityAdminConfirmationStore();
        var administrator = new NetUserId(Guid.NewGuid());
        var otherAdministrator = new NetUserId(Guid.NewGuid());
        var now = TimeSpan.FromSeconds(10);

        var wrongUserToken = store.Issue(administrator, "actor:a", now, TimeSpan.FromSeconds(15));
        Assert.That(store.Consume(otherAdministrator, wrongUserToken, "actor:a", now), Is.False);
        Assert.That(store.Consume(administrator, wrongUserToken, "actor:a", now), Is.True);
        Assert.That(store.Consume(administrator, wrongUserToken, "actor:a", now), Is.False);

        var changedRequestToken = store.Issue(administrator, "actor:a", now, TimeSpan.FromSeconds(15));
        Assert.That(store.Consume(administrator, changedRequestToken, "actor:b", now), Is.False);
        Assert.That(store.Consume(administrator, changedRequestToken, "actor:a", now), Is.False,
            "A mismatched exact-request attempt must consume the token instead of leaving a reusable capability.");
    }

    [Test]
    public void ConfirmationExpiresOnDeadline()
    {
        var store = new NCLivingCityAdminConfirmationStore();
        var administrator = new NetUserId(Guid.NewGuid());
        var issuedAt = TimeSpan.FromSeconds(20);
        var token = store.Issue(administrator, "group:a", issuedAt, TimeSpan.FromSeconds(15));

        Assert.That(store.Consume(administrator, token, "group:a", issuedAt + TimeSpan.FromSeconds(15)), Is.False);
        Assert.That(store.Count, Is.Zero);
    }
}
