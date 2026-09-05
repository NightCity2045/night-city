// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server._NC.Bank;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Bank;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Economy;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCEconomyLedgerSystem))]
public sealed class NCEconomyEmploymentSystemTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: ncZoneSet
          id: NCEconomyEmploymentIntegrationZones
          networkId: 3c91661d-68a9-47b6-917a-8cdac7885fd1
          zones:
          - id: b6ade8ad-0dcc-46e5-b471-72908734bcbb
            kind: District
            name: Economy Test District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: f66930f9-2b05-4391-a4ca-3eabb5545d64
            kind: Street
            parent: b6ade8ad-0dcc-46e5-b471-72908734bcbb
            name: Economy Test Street
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: 6a8f1971-e7ee-472a-b985-bf3557b8c13e
            kind: Building
            parent: f66930f9-2b05-4391-a4ca-3eabb5545d64
            name: Economy Test Workplace
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 1,1
              - 9,1
              - 9,9
              - 1,9
          - id: 62186c66-cc01-444b-a04b-aa8b7081c3ea
            kind: Apartment
            parent: 6a8f1971-e7ee-472a-b985-bf3557b8c13e
            name: Economy Test Home
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 2,2
              - 4,2
              - 4,4
              - 2,4

        - type: ncCityActorArchetype
          id: NCEconomyEmploymentIntegrationActor
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
        """;

    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("62186c66-cc01-444b-a04b-aa8b7081c3ea"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCEconomyEmploymentIntegrationActor");
    private static readonly ProtoId<NCNeuralPersonaProfilePrototype> Persona =
        new("NCDefaultResidentPersona");
    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("3c91661d-68a9-47b6-917a-8cdac7885fd1"));

    [Test]
    public async Task FinanceAdapterReloadsCurrentVersionsAndPagesImmutableHistory()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var directory = server.System<NCPartyDirectorySystem>();
        var ledger = server.System<NCEconomyLedgerSystem>();
        var finance = server.System<NCPartyFinanceAdapterSystem>();
        var database = server.ResolveDependency<IServerDbManager>();
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var organization = NCPartyRef.ForOrganization(NCOrganizationId.New());
        var business = NCPartyRef.ForBusiness(NCBusinessId.New());
        Assert.That((await directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            Guid.NewGuid(), organization, "History Test Organization", Guid.NewGuid(),
            AuditSource: "integration-test"))).Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));
        Assert.That((await directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            Guid.NewGuid(), business, "History Test Business", Guid.NewGuid(),
            AuditSource: "integration-test"))).Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));

        var organizationAccount = await finance.EnsurePrimaryAccountAsync(organization, "integration-test");
        var businessAccount = await finance.EnsurePrimaryAccountAsync(business, "integration-test");
        var clearingAccount = await finance.EnsureIssuanceAccountAsync("integration-test");
        Assert.Multiple(() =>
        {
            Assert.That(organizationAccount.IsResolved, Is.True);
            Assert.That(businessAccount.IsResolved, Is.True);
            Assert.That(clearingAccount.IsResolved, Is.True);
        });

        var openingTransactionId = NCTransactionId.New();
        var opening = await ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            openingTransactionId,
            NCLedgerTransactionKind.OpeningBalance,
            "history test opening balance",
            [
                new NCLedgerLegRequest(clearingAccount.AccountId, clearingAccount.Account!.AggregateVersion, -500),
                new NCLedgerLegRequest(organizationAccount.AccountId, organizationAccount.Account!.AggregateVersion, 500),
            ],
            clearingAccount.Owner,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(opening.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));

        var transferTransactionId = NCTransactionId.New();
        var transfer = await finance.TransferAsync(new NCPartyTransferRequest(
            Guid.NewGuid(),
            transferTransactionId,
            organization,
            business,
            125,
            NCLedgerTransactionKind.Transfer,
            "history test transfer",
            organization,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(transfer.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));

        var firstPage = await finance.GetTransactionHistoryAsync(organization, null, 1);
        var secondPage = await finance.GetTransactionHistoryAsync(
            organization,
            firstPage.NextBeforeAggregateVersion,
            1);
        Assert.Multiple(() =>
        {
            Assert.That(firstPage.IsValid, Is.True);
            Assert.That(firstPage.Entries, Has.Count.EqualTo(1));
            Assert.That(firstPage.Entries[0].Transaction.Snapshot.TransactionId,
                Is.EqualTo(transferTransactionId));
            Assert.That(firstPage.NextBeforeAggregateVersion, Is.EqualTo(3));
            Assert.That(secondPage.IsValid, Is.True);
            Assert.That(secondPage.Entries, Has.Count.EqualTo(1));
            Assert.That(secondPage.Entries[0].Transaction.Snapshot.TransactionId,
                Is.EqualTo(openingTransactionId));
            Assert.That(firstPage.Entries[0].RecordedAt, Is.Not.EqualTo(default(DateTime)));
        });

        // Housing and other aggregate owners use the ledger request directly. This synthetic participant verifies
        // that their state and money commit once under the same idempotent command.
        var currentOrganization = await finance.GetPrimaryAccountAsync(organization, true);
        var currentBusiness = await finance.GetPrimaryAccountAsync(business, true);
        Assert.That(currentOrganization, Is.Not.Null);
        Assert.That(currentBusiness, Is.Not.Null);
        var participantEventId = Guid.NewGuid();
        var participantRequest = new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            NCLedgerTransactionKind.Adjustment,
            "atomic participant test",
            [
                new NCLedgerLegRequest(currentBusiness!.Snapshot.AccountId, currentBusiness.AggregateVersion, -25),
                new NCLedgerLegRequest(currentOrganization!.Snapshot.AccountId, currentOrganization.AggregateVersion, 25),
            ],
            business,
            Guid.NewGuid(),
            AuditSource: "integration-test",
            ParticipantWrites:
            [
                new NCAtomicLedgerParticipantWrite(
                    participantEventId,
                    "living-city.test-settlement.committed",
                    "living-city.test-settlement",
                    "settlement-1",
                    0,
                    1,
                    "{\"state\":\"committed\"}",
                    "{\"amount\":25}"),
            ]);
        var participantApplied = await ledger.PostTransactionAsync(participantRequest);
        var participantDuplicate = await ledger.PostTransactionAsync(participantRequest);
        var participantSnapshot = await database.GetNCAggregateSnapshotAsync(
            "living-city.test-settlement",
            "settlement-1");
        Assert.Multiple(() =>
        {
            Assert.That(participantApplied.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
            Assert.That(participantDuplicate.Status, Is.EqualTo(NCEconomyMutationStatus.Duplicate));
            Assert.That(participantSnapshot?.AggregateVersion, Is.EqualTo(1));
            Assert.That(participantSnapshot?.Payload, Is.EqualTo("{\"state\":\"committed\"}"));
            Assert.That(participantApplied.Accounts.Single(account => account.Snapshot.Owner == organization)
                .Snapshot.Balance, Is.EqualTo(400));
            Assert.That(participantDuplicate.Accounts.Single(account => account.Snapshot.Owner == business)
                .Snapshot.Balance, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task PartyLedgerAndEmploymentRemainTypedBalancedAndIdempotent()
    {
        var server = Pair.Server;
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var directory = server.System<NCPartyDirectorySystem>();
        var resolver = server.System<NCPartyResolverSystem>();
        var ledger = server.System<NCEconomyLedgerSystem>();
        var employment = server.System<NCEmploymentRegistrySystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var serialization = server.ResolveDependency<ISerializationManager>();
        var commandId = Guid.NewGuid();
        var actor = await actors.CreateResidentAsync(new NCCityResidentCreateRequest(
            commandId,
            NCActorId.New(),
            "Mina Ortiz",
            Archetype,
            Persona,
            NCHumanoidProfilePayload.Serialize(serialization, HumanoidCharacterProfile.Random()),
            HomeZone,
            new NCCityActorPhysicalRecoveryState(NetworkId, 5f, 5f, 0, HomeZone),
            commandId,
            AuditSource: "integration-test"));
        Assert.That(actor.Status, Is.EqualTo(NCCityActorMutationStatus.Applied));
        var employeeParty = NCPartyRef.ForActor(actor.ActorId);

        var organizationParty = NCPartyRef.ForOrganization(NCOrganizationId.New());
        var clearingParty = NCPartyRef.ForCityService(NCCityServiceId.New());
        var organizationRegistration = new NCPartyDirectoryRegisterRequest(
            Guid.NewGuid(), organizationParty, "Northside Workshop", Guid.NewGuid(),
            AuditSource: "integration-test");
        var organization = await directory.RegisterAsync(organizationRegistration);
        var duplicateOrganization = await directory.RegisterAsync(organizationRegistration);
        var clearing = await directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            Guid.NewGuid(), clearingParty, "NC Opening Balance Clearing", Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.Multiple(() =>
        {
            Assert.That(organization.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));
            Assert.That(duplicateOrganization.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Duplicate));
            Assert.That(clearing.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));
            Assert.That(NCPartyRef.TryParse(employeeParty.ToString(), out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(employeeParty));
        });

        var employeeResolution = await resolver.ResolveAsync(employeeParty, true);
        var organizationResolution = await resolver.ResolveAsync(organizationParty, true);
        Assert.Multiple(() =>
        {
            Assert.That(employeeResolution.IsResolved, Is.True);
            Assert.That(employeeResolution.DisplayName, Is.EqualTo("Mina Ortiz"));
            Assert.That(organizationResolution.IsResolved, Is.True);
            Assert.That(organizationResolution.DisplayName, Is.EqualTo("Northside Workshop"));
        });

        var invalidLifecycle = await directory.SetLifecycleAsync(new NCPartyDirectoryLifecycleRequest(
            Guid.NewGuid(), organizationParty, 1, (NCPartyLifecycle) byte.MaxValue, Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(invalidLifecycle.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Rejected));

        var employeeAccountId = NCAccountId.New();
        var organizationAccountId = NCAccountId.New();
        var clearingAccountId = NCAccountId.New();
        var employeeAccount = await ledger.CreateAccountAsync(new NCCreateAccountRequest(
            Guid.NewGuid(), employeeAccountId, employeeParty, NCAccountKind.Bank, "eb",
            false, Guid.NewGuid(), AuditSource: "integration-test"));
        var organizationAccount = await ledger.CreateAccountAsync(new NCCreateAccountRequest(
            Guid.NewGuid(), organizationAccountId, organizationParty, NCAccountKind.Organization, "EB",
            false, Guid.NewGuid(), AuditSource: "integration-test"));
        var clearingAccount = await ledger.CreateAccountAsync(new NCCreateAccountRequest(
            Guid.NewGuid(), clearingAccountId, clearingParty, NCAccountKind.SystemClearing, "EB",
            true, Guid.NewGuid(), AuditSource: "integration-test"));
        Assert.Multiple(() =>
        {
            Assert.That(employeeAccount.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
            Assert.That(organizationAccount.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
            Assert.That(clearingAccount.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
        });

        var nullCurrencyAccount = await ledger.CreateAccountAsync(new NCCreateAccountRequest(
            Guid.NewGuid(), NCAccountId.New(), employeeParty, NCAccountKind.Bank, null!,
            false, Guid.NewGuid(), AuditSource: "integration-test"));
        var invalidAccountStatus = await ledger.SetAccountStatusAsync(new NCSetAccountStatusRequest(
            Guid.NewGuid(), employeeAccountId, 1, (NCAccountStatus) byte.MaxValue, employeeParty, Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.Multiple(() =>
        {
            Assert.That(nullCurrencyAccount.Status, Is.EqualTo(NCEconomyMutationStatus.Rejected));
            Assert.That(invalidAccountStatus.Status, Is.EqualTo(NCEconomyMutationStatus.Rejected));
        });

        var openingRequest = new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            NCLedgerTransactionKind.OpeningBalance,
            "organization opening balance",
            [
                new NCLedgerLegRequest(clearingAccountId, 1, -1_000),
                new NCLedgerLegRequest(organizationAccountId, 1, 1_000),
            ],
            clearingParty,
            Guid.NewGuid(),
            AuditSource: "integration-test");
        var opening = await ledger.PostTransactionAsync(openingRequest);
        var duplicateOpening = await ledger.PostTransactionAsync(openingRequest);
        Assert.Multiple(() =>
        {
            Assert.That(opening.Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
            Assert.That(duplicateOpening.Status, Is.EqualTo(NCEconomyMutationStatus.Duplicate));
            Assert.That(opening.Transaction?.Snapshot.Legs.Sum(leg => leg.Amount), Is.Zero);
            Assert.That(opening.Accounts.Single(account => account.Snapshot.AccountId == organizationAccountId)
                .Snapshot.Balance, Is.EqualTo(1_000));
            Assert.That(duplicateOpening.Accounts.Single(account => account.Snapshot.AccountId == clearingAccountId)
                .Snapshot.Balance, Is.EqualTo(-1_000));
        });

        var unbalanced = await ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            NCLedgerTransactionKind.Adjustment,
            "invalid unbalanced write",
            [
                new NCLedgerLegRequest(clearingAccountId, 2, -5),
                new NCLedgerLegRequest(employeeAccountId, 1, 4),
            ],
            clearingParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        var invalidTransactionKind = await ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            (NCLedgerTransactionKind) byte.MaxValue,
            "invalid transaction kind",
            [
                new NCLedgerLegRequest(clearingAccountId, 2, -5),
                new NCLedgerLegRequest(employeeAccountId, 1, 5),
            ],
            clearingParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.Multiple(() =>
        {
            Assert.That(unbalanced.Status, Is.EqualTo(NCEconomyMutationStatus.Rejected));
            Assert.That(invalidTransactionKind.Status, Is.EqualTo(NCEconomyMutationStatus.Rejected));
        });

        var employmentId = NCEmploymentId.New();
        var hireRequest = new NCHirePartyRequest(
            Guid.NewGuid(),
            employmentId,
            employeeParty,
            organizationParty,
            "workshop-tech",
            0,
            organizationAccountId,
            employeeAccountId,
            100,
            TimeSpan.FromMinutes(30).Ticks,
            organizationParty,
            Guid.NewGuid(),
            AuditSource: "integration-test");
        var hired = await employment.HireAsync(hireRequest);
        var duplicateHire = await employment.HireAsync(hireRequest);
        Assert.Multiple(() =>
        {
            Assert.That(hired.Status, Is.EqualTo(NCEmploymentMutationStatus.Applied));
            Assert.That(duplicateHire.Status, Is.EqualTo(NCEmploymentMutationStatus.Duplicate));
            Assert.That(hired.Employment?.Snapshot.Employee, Is.EqualTo(employeeParty));
            Assert.That(hired.Employment?.Snapshot.Employer, Is.EqualTo(organizationParty));
            Assert.That(hired.EmployeeIndex?.Snapshot.CurrentEmploymentId, Is.EqualTo(employmentId));
        });

        var invalidEmploymentStatus = await employment.ChangeAsync(new NCChangeEmploymentRequest(
            Guid.NewGuid(),
            employmentId,
            1,
            1,
            "workshop-tech",
            (NCEmploymentStatus) byte.MaxValue,
            organizationAccountId,
            employeeAccountId,
            100,
            TimeSpan.FromMinutes(30).Ticks,
            organizationParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(invalidEmploymentStatus.Status, Is.EqualTo(NCEmploymentMutationStatus.Rejected));

        var rejectedSecondHire = await employment.HireAsync(hireRequest with
        {
            CommandId = Guid.NewGuid(),
            EmploymentId = NCEmploymentId.New(),
            ExpectedEmployeeIndexVersion = 1,
        });
        Assert.That(rejectedSecondHire.Status, Is.EqualTo(NCEmploymentMutationStatus.Rejected));

        var suspended = await employment.ChangeAsync(new NCChangeEmploymentRequest(
            Guid.NewGuid(),
            employmentId,
            1,
            1,
            "workshop-tech",
            NCEmploymentStatus.Suspended,
            organizationAccountId,
            employeeAccountId,
            100,
            TimeSpan.FromMinutes(30).Ticks,
            organizationParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.That(suspended.Status, Is.EqualTo(NCEmploymentMutationStatus.Applied));

        var terminated = await employment.ChangeAsync(new NCChangeEmploymentRequest(
            Guid.NewGuid(),
            employmentId,
            2,
            2,
            "workshop-tech",
            NCEmploymentStatus.Terminated,
            organizationAccountId,
            employeeAccountId,
            100,
            TimeSpan.FromMinutes(30).Ticks,
            organizationParty,
            Guid.NewGuid(),
            AuditSource: "integration-test"));
        Assert.Multiple(() =>
        {
            Assert.That(terminated.Status, Is.EqualTo(NCEmploymentMutationStatus.Applied));
            Assert.That(terminated.Employment?.Snapshot.Status, Is.EqualTo(NCEmploymentStatus.Terminated));
            Assert.That(terminated.EmployeeIndex?.Snapshot.CurrentEmploymentId.IsValid, Is.False);
        });

        var replacement = await employment.HireAsync(hireRequest with
        {
            CommandId = Guid.NewGuid(),
            EmploymentId = NCEmploymentId.New(),
            PositionId = "shop-supervisor",
            ExpectedEmployeeIndexVersion = 3,
        });
        Assert.That(replacement.Status, Is.EqualTo(NCEmploymentMutationStatus.Applied));

        ledger.UnloadAccount(organizationAccountId);
        ledger.UnloadTransaction(openingRequest.TransactionId);
        employment.Unload(employmentId);
        employment.UnloadEmployeeIndex(employeeParty);
        var reloadedOrganizationAccount = await ledger.GetAccountAsync(organizationAccountId, true);
        var reloadedOpening = await ledger.GetTransactionAsync(openingRequest.TransactionId, true);
        var reloadedCurrentEmployment = await employment.FindCurrentByEmployeeAsync(employeeParty, true);
        Assert.Multiple(() =>
        {
            Assert.That(reloadedOrganizationAccount?.Snapshot.Balance, Is.EqualTo(1_000));
            Assert.That(reloadedOrganizationAccount?.AggregateVersion, Is.EqualTo(2));
            Assert.That(reloadedOpening?.Snapshot.Legs.Sum(leg => leg.Amount), Is.Zero);
            Assert.That(reloadedCurrentEmployment?.Snapshot.PositionId, Is.EqualTo("shop-supervisor"));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
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
