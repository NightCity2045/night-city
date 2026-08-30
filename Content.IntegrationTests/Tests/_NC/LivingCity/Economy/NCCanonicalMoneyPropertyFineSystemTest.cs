// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Economy.Cash;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Database._NC.Police;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Economy;

[TestFixture]
[TestOf(typeof(NCEconomyLedgerSystem))]
[NonParallelizable]
public sealed class NCCanonicalMoneyPropertyFineSystemTest : GameTest
{
    [Test]
    public async Task OpeningRentAndFinePaymentUseOneLedgerAndReplayExactlyOnce()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var finance = server.System<NCPartyFinanceAdapterSystem>();
        var housing = server.System<NCHousingServiceSystem>();
        var properties = server.System<NCPropertyRegistrySystem>();
        var settlements = server.System<NCPropertySettlementSystem>();
        var fines = server.System<NCPoliceFineRegistrySystem>();

        var tenantId = await CreateCharacterAsync(database);
        var officerId = await CreateCharacterAsync(database);
        var tenant = NCPartyRef.ForCharacter(tenantId);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var opened = await finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            tenant,
            1_000,
            "integration-test");
        var replayedOpening = await finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            tenant,
            1_000,
            "integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(opened.Account?.Snapshot.Balance, Is.EqualTo(1_000));
            Assert.That(replayedOpening.Account?.Snapshot.Balance, Is.EqualTo(1_000));
        });

        Assert.That(await housing.EnsureReadyAsync("integration-test"), Is.True);
        var building = new NCBuildingId(new NCZoneId(Guid.NewGuid()));
        var apartment = new NCApartmentId(new NCZoneId(Guid.NewGuid()));
        var registration = await properties.RegisterAsync(new NCRegisterPropertyRequest(
            Guid.NewGuid(),
            apartment,
            building,
            [apartment.ZoneId],
            "integration-property",
            housing.Party,
            0,
            "integration-access",
            new NCPropertyStorageBudget(128, 128, 256, 512),
            "integration-blueprint",
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(registration.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));

        var rentRequest = new NCRentPropertySettlementRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            apartment,
            registration.Property!.AggregateVersion,
            tenant,
            100,
            "integration-rent",
            TimeSpan.FromHours(1).Ticks,
            "integration-interior",
            1,
            NCPropertyPermission.All,
            Guid.NewGuid(),
            Guid.NewGuid(),
            AuditSource: "integration-test");
        var rented = await settlements.RentAsync(rentRequest);
        var replayedRent = await settlements.RentAsync(rentRequest);
        Assert.Multiple(() =>
        {
            Assert.That(rented.Status, Is.EqualTo(NCPropertySettlementStatus.Applied));
            Assert.That(replayedRent.Status, Is.EqualTo(NCPropertySettlementStatus.Duplicate));
            Assert.That(replayedRent.Property?.Snapshot.Status, Is.EqualTo(NCPropertyStatus.Rented));
            Assert.That(replayedRent.Property?.Snapshot.LastSettlementTransactionId,
                Is.EqualTo(rentRequest.TransactionId));
        });

        var issued = await fines.CreateAsync(
            tenant,
            "NC-2045-TEST",
            "Integration fine",
            250,
            TimeSpan.FromHours(2),
            NCPartyRef.ForCharacter(officerId),
            "integration-test");
        Assert.That(issued.Status, Is.EqualTo(NCPoliceFineMutationStatus.Applied));
        var paid = await fines.PayAsync(issued.Fine!.Snapshot.FineId, tenant);
        var replayedPayment = await fines.PayAsync(issued.Fine.Snapshot.FineId, tenant);

        var tenantAccount = await finance.GetPrimaryAccountAsync(tenant, true);
        var housingAccount = await finance.GetPrimaryAccountAsync(housing.Party, true);
        var ncpd = await finance.EnsureOrganizationAsync("ncpd", "Night City Police Department", "integration-test");
        var persistedFine = await fines.GetAsync(issued.Fine.Snapshot.FineId, true);
        var paymentHistory = await finance.GetTransactionHistoryAsync(tenant, null, 20);
        Assert.Multiple(() =>
        {
            Assert.That(paid.Result, Is.EqualTo(NCPoliceFinePaymentResult.Success));
            Assert.That(replayedPayment.Result, Is.EqualTo(NCPoliceFinePaymentResult.Success));
            Assert.That(tenantAccount?.Snapshot.Balance, Is.EqualTo(650));
            Assert.That(housingAccount?.Snapshot.Balance, Is.EqualTo(100));
            Assert.That(ncpd.Account?.Snapshot.Balance, Is.EqualTo(250));
            Assert.That(persistedFine?.Snapshot.Status, Is.EqualTo(NCPoliceFineStatus.Paid));
            Assert.That(persistedFine?.Snapshot.PaymentTransactionId, Is.Not.Null);
            Assert.That(paymentHistory.Entries.Count(value =>
                    value.Transaction.Snapshot.TransactionId == persistedFine!.Snapshot.PaymentTransactionId),
                Is.EqualTo(1));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    [Test]
    public async Task CashHandoffsReplayRollbackAndRecoverAcrossRoundEpochs()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var finance = server.System<NCPartyFinanceAdapterSystem>();
        var handoffs = server.System<NCCashHandoffSystem>();
        var characterId = await CreateCharacterAsync(database);
        var party = NCPartyRef.ForCharacter(characterId);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && handoffs.IsReady,
            "The first cash-handoff epoch did not become ready.");
        Assert.That((await finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            party,
            1_000,
            "cash-handoff-integration-test")).Account?.Snapshot.Balance, Is.EqualTo(1_000));

        var withdrawalId = NCCashHandoffId.New();
        var withdrawalRequest = new NCStartCashHandoffRequest(
            Guid.NewGuid(),
            withdrawalId,
            NCTransactionId.New(),
            party,
            NCCashHandoffDirection.Withdrawal,
            -300,
            300,
            0,
            party,
            "integration withdrawal",
            Guid.NewGuid(),
            AuditSource: "cash-handoff-integration-test");
        var withdrawal = await handoffs.StartAsync(withdrawalRequest);
        var replayedWithdrawal = await handoffs.StartAsync(withdrawalRequest);
        Assert.Multiple(() =>
        {
            Assert.That(withdrawal.Status, Is.EqualTo(NCCashHandoffMutationStatus.Applied));
            Assert.That(replayedWithdrawal.Status, Is.EqualTo(NCCashHandoffMutationStatus.Duplicate));
            Assert.That(replayedWithdrawal.Handoff?.Snapshot.Status,
                Is.EqualTo(NCCashHandoffStatus.PendingPhysical));
        });
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(700));

        var reversed = await handoffs.ReverseAsync(withdrawalId, party, "cash-handoff-integration-test");
        var replayedReversal = await handoffs.ReverseAsync(withdrawalId, party, "cash-handoff-integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(reversed.Status, Is.EqualTo(NCCashHandoffMutationStatus.Applied));
            Assert.That(replayedReversal.Status, Is.EqualTo(NCCashHandoffMutationStatus.Duplicate));
            Assert.That(replayedReversal.Handoff?.Snapshot.Status, Is.EqualTo(NCCashHandoffStatus.Reversed));
            Assert.That(replayedReversal.Handoff?.Snapshot.ReversalTransactionId, Is.Not.Null);
        });
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(1_000));

        var depositId = NCCashHandoffId.New();
        var deposit = await handoffs.StartAsync(new NCStartCashHandoffRequest(
            Guid.NewGuid(),
            depositId,
            NCTransactionId.New(),
            party,
            NCCashHandoffDirection.Deposit,
            450,
            500,
            50,
            party,
            "integration deposit",
            Guid.NewGuid(),
            AuditSource: "cash-handoff-integration-test"));
        var completed = await handoffs.CompleteAsync(depositId, party, "cash-handoff-integration-test");
        var replayedCompletion = await handoffs.CompleteAsync(depositId, party, "cash-handoff-integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(deposit.Status, Is.EqualTo(NCCashHandoffMutationStatus.Applied));
            Assert.That(completed.Status, Is.EqualTo(NCCashHandoffMutationStatus.Applied));
            Assert.That(replayedCompletion.Status, Is.EqualTo(NCCashHandoffMutationStatus.Duplicate));
            Assert.That(replayedCompletion.Handoff?.Snapshot.Status, Is.EqualTo(NCCashHandoffStatus.Completed));
            Assert.That(replayedCompletion.Handoff?.Snapshot.TaxAmount, Is.EqualTo(50));
        });
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(1_450));

        var interruptedWithdrawalId = NCCashHandoffId.New();
        var interruptedDepositId = NCCashHandoffId.New();
        Assert.That((await handoffs.StartAsync(new NCStartCashHandoffRequest(
            Guid.NewGuid(),
            interruptedWithdrawalId,
            NCTransactionId.New(),
            party,
            NCCashHandoffDirection.Withdrawal,
            -200,
            200,
            0,
            party,
            "interrupted withdrawal",
            Guid.NewGuid(),
            AuditSource: "cash-handoff-integration-test"))).Status,
            Is.EqualTo(NCCashHandoffMutationStatus.Applied));
        Assert.That((await handoffs.StartAsync(new NCStartCashHandoffRequest(
            Guid.NewGuid(),
            interruptedDepositId,
            NCTransactionId.New(),
            party,
            NCCashHandoffDirection.Deposit,
            100,
            100,
            0,
            party,
            "interrupted deposit",
            Guid.NewGuid(),
            AuditSource: "cash-handoff-integration-test"))).Status,
            Is.EqualTo(NCCashHandoffMutationStatus.Applied));
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(1_350));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The first cash-handoff epoch did not close.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && handoffs.IsReady,
            "Prior cash handoffs were not recovered in the second epoch.", attempts: 360);

        var recoveredWithdrawal = await handoffs.GetAsync(interruptedWithdrawalId, true);
        var recoveredDeposit = await handoffs.GetAsync(interruptedDepositId, true);
        Assert.Multiple(() =>
        {
            Assert.That(recoveredWithdrawal?.Snapshot.Status, Is.EqualTo(NCCashHandoffStatus.Reversed));
            Assert.That(recoveredWithdrawal?.Snapshot.ReversalTransactionId, Is.Not.Null);
            Assert.That(recoveredDeposit?.Snapshot.Status, Is.EqualTo(NCCashHandoffStatus.Completed));
        });
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(1_550));
        Assert.That(await database.GetNCAggregateSnapshotAsync(
            "living-city.cash-handoff-recovery",
            "global"), Is.Not.Null);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The second cash-handoff epoch did not close.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PostRound, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && handoffs.IsReady,
            "The watermarked cash recovery did not become ready in the third epoch.", attempts: 360);
        Assert.That((await finance.GetPrimaryAccountAsync(party, true))?.Snapshot.Balance, Is.EqualTo(1_550));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(
            new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(),
            CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }

        Assert.Fail(failure);
    }

}
