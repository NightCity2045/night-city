// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Contracts;
using Content.Server._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Economy;
using System.Text.Json;
using Content.Shared._NC.Identity;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[NonParallelizable]
public sealed partial class NCMetaOrganizationAuthorityTest : GameTest
{
    [TestPrototypes]
    private const string Hardware = """
        - type: entity
          id: NCMetaIntegrationOrganizationHost
          components:
          - type: NCMetaHost
            profile: NCMetaNetworkHost
          - type: NCMetaStorage
        """;

    [Test]
    public async Task NativeDepartmentManagementUsesPlayerEmploymentAndRejectsDemotionAndRoleAbaAtCommit()
    {
        var database = Server.ResolveDependency<IServerDbManager>();
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var authority = Server.System<NCMetaAuthoritySystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var directory = Server.System<NCPartyDirectorySystem>();
        DepartmentPrototype department = null!;
        NCCharacterId character = default;
        EntityUid user = default, host = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(ServerSession, Is.Not.Null);
            Assert.That(preferences.TryGetSelectedNCCharacterId(ServerSession!.UserId, out character), Is.True);
            department = prototypes.EnumeratePrototypes<DepartmentPrototype>()
                .OrderBy(value => value.ID, StringComparer.Ordinal).First(value => value.NCPersonnelManagers.Count > 0);
            var maps = Server.System<SharedMapSystem>();
            maps.CreateMap(out var map);
            user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
            host = SEntMan.SpawnEntity("NCMetaIntegrationOrganizationHost", new MapCoordinates(Vector2.Zero, map));
            Server.PlayerMan.SetAttachedEntity(ServerSession, user);
            SEntMan.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
        });
        var clock = Server.System<NCActiveWorldTimeSystem>();
        for (var step = 0; step < 180 && !clock.IsReady; step++) await Server.WaitRunTicks(1);
        var managerJob = department.NCPersonnelManagers.First();
        var employeeJob = prototypes.EnumeratePrototypes<JobPrototype>().First(value =>
            !department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(value.ID))).ID;
        Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, managerJob), Is.True);
        var owner = NCPersistentIdentity.Organization(department.ID);
        if (await directory.GetAsync(owner) == null)
        {
            var registered = await directory.RegisterAsync(new(Guid.NewGuid(), owner, "Authority fixture", Guid.NewGuid()));
            Assert.That(registered.Status, Is.EqualTo(NCPartyDirectoryMutationStatus.Applied), registered.Error);
        }
        NCMetaDeviceEntry entry = null!;
        for (var step = 0; step < 180; step++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = devices.TryGetEntry(host, out entry) && entry.Ready);
            if (ready) break;
            await Server.WaitRunTicks(1);
        }
        Assert.That(entry?.Ready, Is.True);
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch = default;
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out now), Is.True));
        var device = await persistence.ClaimDeviceAsync(Guid.NewGuid(), entry.Id, owner.ToString(), "Organization", epoch, now);
        Assert.That(device, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(devices.TryApplyOwner(host, entry.Id, "", owner.ToString(), device!.AuthorityEpoch), Is.True));
        var proof = await authority.CaptureManagementAsync(user, owner.ToString());
        Assert.That(proof?.Employment?.CharacterId, Is.EqualTo(character.Value));
        NCMetaAuthorityConstraints constraints = null!;
        await Server.WaitAssertion(() =>
        {
            Assert.That(devices.CanManage(host, user), Is.True);
            constraints = authority.CaptureWriteConstraints(proof!, host)!;
            Assert.That(constraints, Is.Not.Null);
        });
        var finance = Server.System<NCPartyFinanceAdapterSystem>();
        var ledger = Server.System<NCEconomyLedgerSystem>();
        var credit = Guid.NewGuid();
        Assert.That((await finance.AdjustWithIssuanceAsync(new(credit,
            NCPersistentIdentity.Transaction("meta", "org-contract-credit", credit.ToString("D")), owner, 100,
            NCLedgerTransactionKind.Adjustment, "native authority fixture", owner, credit))).Status,
            Is.EqualTo(NCEconomyMutationStatus.Applied));
        var payer = (await finance.GetPrimaryAccountAsync(owner, true))!;
        var escrow = NCPersistentIdentity.SystemAccount("meta-contract-authority-test." + Guid.NewGuid().ToString("D"));
        Assert.That((await ledger.CreateAccountAsync(new(Guid.NewGuid(), escrow, owner, NCAccountKind.Escrow,
            finance.Configuration.CurrencyId, false, Guid.NewGuid()))).Status, Is.EqualTo(NCEconomyMutationStatus.Applied));
        async Task<NCEconomyMutationResult> TryFundWithProof(NCMetaAuthorityConstraints captured, bool accepted)
        {
            var request = Guid.NewGuid();
            var transaction = NCPersistentIdentity.Transaction("meta", "org-contract-authority", request.ToString("D"));
            var held = (await ledger.GetAccountAsync(escrow, true))!;
            var result = await ledger.PostTransactionAsync(new(request, transaction, NCLedgerTransactionKind.Transfer,
                "meta-contract:authority-regression", [new(payer.Snapshot.AccountId, payer.AggregateVersion, -100),
                    new(escrow, held.AggregateVersion, 100)], proof!.Actor, request,
                ParticipantWrites: [new(Guid.NewGuid(), "meta.development.admitted", "meta.development.command", request.ToString("D"),
                    0, 1, JsonSerializer.Serialize(new NCMetaDevelopmentCommand(proof!.Actor.ToString(), new string('a', 64), null, 1)), "{}")],
                Authority: captured));
            Assert.That(result.Status, Is.EqualTo(accepted ? NCEconomyMutationStatus.Applied : NCEconomyMutationStatus.VersionConflict));
            Assert.That(await ledger.GetTransactionAsync(transaction, true) != null, Is.EqualTo(accepted));
            Assert.That(await persistence.ReadSnapshotAsync("meta.development.command", request.ToString("D")) != null, Is.EqualTo(accepted));
            Assert.That((await finance.GetPrimaryAccountAsync(owner, true))!.Snapshot.Balance, Is.EqualTo(payer.Snapshot.Balance - (accepted ? 100 : 0)));
            Assert.That((await ledger.GetAccountAsync(escrow, true))!.Snapshot.Balance, Is.EqualTo(accepted ? 100 : 0));
            return result;
        }
        var project = (await persistence.ReadProjectAsync(device!.ActiveProject, owner.ToString()))!;
        var file = project.Files.Single();
        var previous = await persistence.ReadSourceAsync(project, file.Id);
        Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, new ProtoId<JobPrototype>(employeeJob)), Is.True);
        await Server.WaitAssertion(() =>
        {
            Assert.That(authority.IsCurrent(proof!), Is.False);
            Assert.That(devices.CanManage(host, user), Is.False);
        });
        await TryFundWithProof(constraints, false);
        var denied = await persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, file.Id, owner.ToString(), file.Revision,
            file.Path, "module denied; fn main()->void{}", epoch, now, constraints);
        Assert.That(denied.Success, Is.False);
        Assert.That(await persistence.ReadSourceAsync((await persistence.ReadProjectAsync(project.Id, owner.ToString()))!, file.Id), Is.EqualTo(previous));
        Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, managerJob), Is.True);
        await Server.WaitAssertion(() => Assert.That(authority.IsCurrent(proof!), Is.False, "Role ABA cannot revive an issued proof."));
        await TryFundWithProof(constraints, false);
        var renewed = await authority.CaptureManagementAsync(user, owner.ToString());
        Assert.That(renewed, Is.Not.Null);
        Assert.That(renewed!.Employment!.LastEventId, Is.GreaterThan(proof!.Employment!.LastEventId));
        await Server.WaitAssertion(() => Assert.That(devices.CanManage(host, user), Is.True));
        NCMetaAuthorityConstraints renewedConstraints = null!;
        await Server.WaitAssertion(() => renewedConstraints = authority.CaptureWriteConstraints(renewed!, host)!);
        await TryFundWithProof(renewedConstraints, true);
    }
}
