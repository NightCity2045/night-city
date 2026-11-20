// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Lifecycle;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Lifecycle;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.UnitTesting;


[TestFixture]
[TestOf(typeof(NCEstateRegistrySystem))]
[NonParallelizable]
public sealed class NCEstateRegistrySystemTest : GameTest
{
    [Test]
    public async Task EstateRequiresAssetsAndClaimsToBeResolvedBeforeClosing()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var estates = server.System<NCEstateRegistrySystem>();
        var reporter = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var beneficiary = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        Assert.That(clock.TryGetCurrentTime(out _, out var now), Is.True);

        var deathEvent = Guid.NewGuid();
        var estate = await estates.OpenAsync(reporter, beneficiary, deathEvent, "Reporter estate");
        Assert.That(estate, Is.Not.Null);
        Assert.That((await estates.OpenAsync(reporter, beneficiary, deathEvent, "Reporter estate"))?.Snapshot.EstateId,
            Is.EqualTo(estate!.Snapshot.EstateId));
        var withAsset = await estates.AddAssetAsync(estate.Snapshot.EstateId, estate.AggregateVersion,
            new NCEstateAssetRef(NCEstateAssetKind.Item, "item:test-heirloom", 500, false));
        var claimId = Guid.NewGuid();
        var withClaim = await estates.FileClaimAsync(estate.Snapshot.EstateId, withAsset!.AggregateVersion,
            new NCEstateClaim(claimId, beneficiary, 100, "funeral-cost", NCEstateClaimStatus.Filed, now));
        Assert.That(await estates.CloseAsync(estate.Snapshot.EstateId, withClaim!.AggregateVersion), Is.Null,
            "An estate with unresolved assets and claims must remain open.");
        var transferred = await estates.SetAssetTransferredAsync(estate.Snapshot.EstateId, withClaim.AggregateVersion,
            NCEstateAssetKind.Item, "item:test-heirloom");
        var paid = await estates.SetClaimStatusAsync(estate.Snapshot.EstateId, transferred!.AggregateVersion, claimId,
            NCEstateClaimStatus.Paid);
        var closed = await estates.CloseAsync(estate.Snapshot.EstateId, paid!.AggregateVersion);
        Assert.That(closed?.Snapshot.Status, Is.EqualTo(NCEstateStatus.Closed));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(), CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate, string failure, int attempts = 240)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete) return;
        }
        Assert.Fail(failure);
    }
}
