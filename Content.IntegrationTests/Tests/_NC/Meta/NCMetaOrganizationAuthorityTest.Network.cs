// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Identity;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaOrganizationAuthorityTest
{
    [Test]
    public async Task RealOrganizationNetConfigurationAndItsSqlParticipantsRejectDemotionAndSameRoleAba()
    {
        Task<T> Native<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
        var db = Server.ResolveDependency<IServerDbManager>();
        var authority = Server.System<NCMetaAuthoritySystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var net = Server.System<NCMetaNetrunSystem>();
        var scenes = Server.System<NCMetaDemoSceneSystem>();
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        DepartmentPrototype department = null!;
        ProtoId<JobPrototype> manager = default;
        string employee = "";
        NCCharacterId character = default;
        NCMetaDemoSceneInstance physical = null!;
        NCSessionEpoch epoch = default;
        NCActiveWorldTime now = default;
        Content.Shared._NC.Persistence.Identity.NCPartyRef owner = default;
        async Task Wait(Func<bool> check)
        {
            for (var index = 0; index < 900; index++)
            { var ready = false; await Server.WaitPost(() => ready = check()); if (ready) return; await Server.WaitRunTicks(1); }
            Assert.Fail("The actual native organization NET setup did not complete.");
        }
        try
        {
            await Server.WaitAssertion(() =>
            {
                Assert.That(ServerSession, Is.Not.Null);
                Assert.That(preferences.TryGetSelectedNCCharacterId(ServerSession!.UserId, out character), Is.True);
                department = SProtoMan.EnumeratePrototypes<DepartmentPrototype>().OrderBy(value => value.ID, StringComparer.Ordinal)
                    .First(value => value.NCPersonnelManagers.Count != 0);
                manager = department.NCPersonnelManagers.First();
                employee = SProtoMan.EnumeratePrototypes<JobPrototype>().First(value =>
                    !department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(value.ID))).ID;
                owner = NCPersistentIdentity.Organization(department.ID);
                SEntMan.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
                Assert.That(scenes.TryCreate("NCMetaNetScene", out physical), Is.True);
                Server.PlayerMan.SetAttachedEntity(ServerSession, physical.Operator);
            });
            await Wait(() => Server.System<NCActiveWorldTimeSystem>().IsReady);
            Assert.That(await Native(() => db.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, manager)), Is.True);
            var directory = Server.System<NCPartyDirectorySystem>();
            if (await Native(() => directory.GetAsync(owner)) == null)
                Assert.That((await Native(() => directory.RegisterAsync(new(Guid.NewGuid(), owner, "NET authority fixture", Guid.NewGuid())))).Status,
                    Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));
            Assert.That(await Native(() => authority.CaptureManagementAsync(physical.Operator, owner.ToString())), Is.Not.Null);
            foreach (var endpoint in physical.Objects.Values)
            {
                NCMetaDeviceEntry entry = null!;
                await Wait(() => devices.TryGetEntry(endpoint, out entry) && entry.Ready && devices.IsPowered(endpoint));
                await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out now), Is.True));
                var claimed = await Native(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), entry.Id, owner.ToString(), "Organization NET", epoch, now));
                Assert.That(claimed, Is.Not.Null);
                await Server.WaitAssertion(() => Assert.That(devices.TryApplyOwner(endpoint, entry.Id, "", owner.ToString(), claimed!.AuthorityEpoch), Is.True));
            }
            var host = physical.Objects["host"]; var node = physical.Objects["control"]; var door = physical.Objects["door"];
            string accessId = "";
            await Server.WaitAssertion(() =>
            {
                Server.System<SharedTransformSystem>().SetCoordinates(physical.Operator, SComp<TransformComponent>(host).Coordinates);
                Assert.That(devices.TryGetEntry(host, out var entry), Is.True); accessId = entry.Id;
                Assert.That(net.TrySynchronizeSecurityPolicy(host, physical.Operator), Is.True);
            });
            NCAggregateSnapshotData? baseline = null;
            for (var index = 0; index < 900 && baseline == null; index++)
            { baseline = await Native(() => persistence.ReadSnapshotAsync("meta.net.policy", accessId)); if (baseline == null) await Server.WaitRunTicks(1); }
            Assert.That(baseline, Is.Not.Null);
            var deck = physical.Objects["deck"];
            NCMetaNetState connected = null!;
            await Wait(() =>
            {
                Server.System<SharedTransformSystem>().SetCoordinates(physical.Operator, SComp<TransformComponent>(deck).Coordinates);
                if (net.TryGetState(deck, physical.Operator, out var present) && present.SessionId != Guid.Empty)
                { connected = present; return true; }
                return net.TryConnect(deck, physical.Operator, host) && net.TryGetState(deck, physical.Operator, out connected) && connected.SessionId != Guid.Empty;
            });
            var proof = await Native(() => authority.CaptureManagementAsync(physical.Operator, owner.ToString()));
            NCMetaAuthorityConstraints captured = null!;
            await Server.WaitAssertion(() =>
            {
                Assert.That(proof?.Employment?.CharacterId, Is.EqualTo(character.Value));
                captured = authority.CaptureWriteConstraints(proof!, host, node, door)!;
                Assert.That(captured, Is.Not.Null);
                Assert.That(persistence.TryCaptureTime(out epoch, out now), Is.True);
            });
            async Task RejectStaleSqlWrite()
            {
                var payload = JsonNode.Parse(baseline!.Payload)!.AsObject();
                payload["Revision"] = baseline.AggregateVersion + 1;
                var request = Guid.NewGuid(); var witness = request.ToString("D");
                var result = await Native(() => db.ApplyNCPersistentBatchMutationAsync(new(request, "meta.test.net-stale-management", "meta.owner", owner.ToString(),
                    [new(Guid.NewGuid(), "meta.net.policy.updated", "meta.net.policy", accessId, baseline.AggregateVersion, 1, payload.ToJsonString(), "{}"),
                        new(Guid.NewGuid(), "meta.test.net-witness", "meta.test.net-witness", witness, 0, 1, JsonSerializer.Serialize(new { Applied = true }), "{}")],
                    "{}", request, null, epoch, now, [], Authority: captured)));
                Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
                Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.net.policy", accessId)), Is.EqualTo(baseline));
                Assert.That(await Native(() => db.GetNCAggregateSnapshotAsync("meta.test.net-witness", witness)), Is.Null);
                Assert.That(await Native(() => db.GetNCPersistentCommandResultAsync(request)), Is.Null);
            }
            Assert.That(await Native(() => db.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, new ProtoId<JobPrototype>(employee))), Is.True);
            await Server.WaitAssertion(() =>
            {
                Assert.That(authority.IsCurrent(proof!), Is.False);
                Assert.That(net.TrySynchronizeSecurityPolicy(host, physical.Operator), Is.False);
                Server.System<SharedTransformSystem>().SetCoordinates(physical.Operator, SComp<TransformComponent>(node).Coordinates);
                Assert.That(net.TryBindControlNode(node, door, physical.Operator), Is.False);
            });
            await RejectStaleSqlWrite();
            Assert.That(await Native(() => db.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, manager)), Is.True);
            await Server.WaitAssertion(() => Assert.That(authority.IsCurrent(proof!), Is.False, "Returning to the same native job cannot revive an old SQL proof."));
            await RejectStaleSqlWrite();
            Assert.That(await Native(() => authority.CaptureManagementAsync(physical.Operator, owner.ToString())), Is.Not.Null);
            await Server.WaitAssertion(() =>
            {
                Server.System<SharedTransformSystem>().SetCoordinates(physical.Operator, SComp<TransformComponent>(deck).Coordinates);
                Assert.That(net.TryGetState(deck, physical.Operator, out var afterAba), Is.True);
                Assert.That(afterAba.SessionId, Is.EqualTo(Guid.Empty), "Fresh management permission cannot revive the prior native NET session.");
                Assert.That(net.TryExecuteAction(deck, physical.Operator, NCMetaNetOperation.Scan, connected.SessionId, connected.Revision), Is.False);
            });
            await Wait(() => net.TryConnect(deck, physical.Operator, host) && net.TryGetState(deck, physical.Operator, out var fresh) && fresh.SessionId != Guid.Empty && fresh.SessionId != connected.SessionId);
            await Server.WaitPost(() => Server.System<SharedTransformSystem>().SetCoordinates(physical.Operator, SComp<TransformComponent>(node).Coordinates));
            await Wait(() => net.TryBindControlNode(node, door, physical.Operator));
            await Wait(() => devices.TryGetEntry(door, out var target) && SComp<NCMetaNetNodeComponent>(node).ControlledDeviceId == target.Id);
        }
        finally
        {
            await Server.WaitPost(() => { if (physical != null) scenes.TryDestroy(physical.Id); });
            await Server.WaitRunTicks(2);
        }
    }
}
