// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Fixers;
using Content.Server._NC.Identity;
using Content.Shared._NC.Fixers;
using Content.Shared._NC.Identity;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Fixers;

[TestFixture]
[TestOf(typeof(NCFixerMissionSystem))]
[NonParallelizable]
public sealed class NCFixerMissionTest : GameTest
{
    // A beating job on a bare mob without NC medicine, so the test can set the outcome directly.
    [TestPrototypes]
    private const string Prototypes = @"
- type: ncFixerMission
  id: NCTestFixerVladBeating
  fixer: NCFixerVlad
  kind: Beating
  name: nc-fixer-mission-vlad-debtor
  owner: Neutral
  item: NCTestBeatingDummy
  reward: 100
  reputationGain: 3
  reputationLoss: 4
  weight: 0

- type: entity
  id: NCTestBeatingDummy
  components:
  - type: MobState
    allowedStates: [ Alive, Critical, Dead ]
";

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    [Test]
    public async Task MissionPrototypesAreConsistent()
    {
        await Server.WaitAssertion(() =>
        {
            foreach (var mission in SProtoMan.EnumeratePrototypes<NCFixerMissionPrototype>())
            {
                Assert.That(mission.Reward, Is.GreaterThan(0), mission.ID);
                Assert.That(SProtoMan.HasIndex(mission.Fixer), mission.ID);
                if (mission.Kind == NCFixerMissionKind.Collection)
                    Assert.That(mission.Requirements, Is.Not.Empty, mission.ID);
                else if (mission.Kind == NCFixerMissionKind.Recon)
                    Assert.That(mission.Owner, Is.Not.Null, mission.ID);
                else if (mission.Kind == NCFixerMissionKind.Laundering)
                    Assert.That(mission.LaunderAmount > 0 && mission.LaunderAccount != null, mission.ID);
                else if (mission.TargetDepartments.Count == 0)
                    Assert.That(mission.Item, Is.Not.Null, mission.ID);

                if (mission.Terminal != null)
                    Assert.That(mission.Kind, Is.EqualTo(NCFixerMissionKind.Retrieval), mission.ID);
                if (mission.ItemFromFixer)
                    Assert.That(mission.Kind == NCFixerMissionKind.Delivery && mission.DropAtMarker, mission.ID);
            }
        });
    }

    private (EntityUid Worker, int Profile) SpawnWorker(EntityCoordinates coords, int profile)
    {
        var minds = Server.System<SharedMindSystem>();
        var worker = SEntMan.SpawnEntity("MobHuman", coords);
        SEntMan.EnsureComponent<MindContainerComponent>(worker);
        var mind = minds.CreateMind(null).Owner;
        minds.TransferTo(mind, worker);
        SEntMan.EnsureComponent<NCCharacterIdentityComponent>(mind).CharacterId = new NCCharacterId(profile);
        return (worker, profile);
    }

    /// <summary>
    /// Vlad's push: the brick goes straight into the courier's hands, the buyer's drop appears at a dead drop
    /// marker, and Vlad pays cash in person when the courier comes back.
    /// </summary>
    [Test]
    public async Task PushPaysInPerson()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var hands = Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var map = await Pair.CreateTestMap();
        Entity<NCFixerComponent> fixer = default;
        EntityUid worker = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerVlad", coords);
            SEntMan.SpawnEntity("NCMissionMarkerDeadDropNeutral", coords.Offset(new System.Numerics.Vector2(4, 0)));
            (worker, profile) = SpawnWorker(coords, 900001);
            fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            Assert.That(missions.TrySetOffer("NCFixerVlad", 0, "NCFixerVladPush"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            Assert.That(missions.TryAccept(fixer, 1, worker), Is.False, "One job at a time.");

            var spawned = missions.GetMissionEntities(profile);
            var brick = spawned.First(uid => SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "NCMissionDrugBrick");
            Assert.That(hands.IsHolding(worker, brick), "Vlad hands the brick over.");
            var drop = spawned.First(uid => SEntMan.HasComponent<NCFixerDeadDropComponent>(uid));
            Assert.That(Server.System<SharedEntityStorageSystem>().Insert(brick, drop), Is.True);
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment));
            Assert.That(missions.TryCollectPayment(fixer, worker), Is.True);
            Assert.That(missions.TryGetActiveMission(profile, out _, out _), Is.False);
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(2));
        });
    }

    /// <summary>
    /// An Insider hacking job behind a reputation gate: a terminal and its guards appear, hacking gives the
    /// shard, and the shard in the Insider's drop completes the job.
    /// </summary>
    [Test]
    public async Task HackedShardCompletesRetrieval()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid drop = default;
        EntityUid shard = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerInsider", coords);
            drop = SEntMan.SpawnEntity("NCFixerDeadDropInsider", coords.Offset(new System.Numerics.Vector2(1, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveTygerClaws", coords.Offset(new System.Numerics.Vector2(6, 0)));
            (var worker, profile) = SpawnWorker(coords, 900002);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            Assert.That(missions.TrySetOffer("NCFixerInsider", 0, "NCFixerInsiderTygerNames"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.False, "Not trusted without street reputation.");
            reputation.AddReputation(profile, 10);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);

            var spawned = missions.GetMissionEntities(profile);
            Assert.That(spawned.Count(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid)), Is.GreaterThan(0), "The terminal is guarded.");
            var terminal = spawned.First(uid => SEntMan.HasComponent<NCMissionTerminalComponent>(uid));
            var terminalEnt = new Entity<NCMissionTerminalComponent>(terminal, SEntMan.GetComponent<NCMissionTerminalComponent>(terminal));
            Assert.That(missions.CompleteHack(terminalEnt, worker), Is.True);
            Assert.That(missions.CompleteHack(terminalEnt, worker), Is.False, "A terminal gives one shard.");

            shard = missions.GetMissionEntities(profile).First(uid => SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "NCMissionDataShard");
            Assert.That(Server.System<SharedEntityStorageSystem>().Insert(shard, drop), Is.True);
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            // No bank account in a test: the wire fails and the Insider owes the money until it can be collected.
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment));
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(14));
            Assert.That(SEntMan.Deleted(shard) || SEntMan.IsQueuedForDeletion(shard), "The shard is taken.");
        });
    }

    /// <summary>
    /// Vlad's sabotage of the Medical Technologies limb farm: the vat controller appears at the objective with
    /// armed staff at the guard posts, and destroying it completes the job.
    /// </summary>
    [Test]
    public async Task DestroyedVatControllerCompletesSabotage()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerVlad", coords);
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveMedicalTechnologies", coords.Offset(new System.Numerics.Vector2(6, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerGuardMedicalTechnologies", coords.Offset(new System.Numerics.Vector2(6, 3)));
            SEntMan.SpawnEntity("NCMissionMarkerGuardMedicalTechnologies", coords.Offset(new System.Numerics.Vector2(9, 0)));
            (var worker, profile) = SpawnWorker(coords, 900004);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            Assert.That(missions.TrySetOffer("NCFixerVlad", 0, "NCFixerVladMedTechVats"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.False, "Not trusted without street reputation.");
            reputation.AddReputation(profile, 25);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);

            var spawned = missions.GetMissionEntities(profile);
            Assert.That(spawned.Count(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid)), Is.GreaterThanOrEqualTo(7),
                "The lab staff hold the place.");
            var controller = spawned.First(uid => SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == "NCMedTechVatController");
            SEntMan.DeleteEntity(controller);
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            // Vlad pays in person once the saboteur comes back.
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment));
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(31));
        });
    }

    /// <summary>A beating counts when the debtor is down but alive; killing the debtor fails the job.</summary>
    [Test]
    public async Task BeatingNeedsTheDebtorAlive()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var mobState = Server.System<Content.Shared.Mobs.Systems.MobStateSystem>();
        var map = await Pair.CreateTestMap();
        var profiles = new int[2];

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerVlad", coords);
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveNeutral", coords.Offset(new System.Numerics.Vector2(8, 0)));
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));

            for (var i = 0; i < 2; i++)
            {
                var (worker, profile) = SpawnWorker(coords, 900010 + i);
                profiles[i] = profile;
                reputation.AddReputation(profile, 10);
                Assert.That(missions.TrySetOffer("NCFixerVlad", 0, "NCTestFixerVladBeating"), Is.True);
                Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
                mobState.ChangeMobState(missions.GetMissionEntities(profile).First(), i == 0
                    ? Content.Shared.Mobs.MobState.Critical
                    : Content.Shared.Mobs.MobState.Dead);
            }
        });

        await Pair.RunSeconds(2);

        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profiles[0], out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment), "Down but alive: done.");
            Assert.That(reputation.GetReputation(profiles[0]), Is.EqualTo(13));
            Assert.That(missions.TryGetActiveMission(profiles[1], out _, out _), Is.False, "Dead debtor: the job failed.");
            Assert.That(reputation.GetReputation(profiles[1]), Is.EqualTo(6));
        });
    }

    /// <summary>The debtor itself is a human whose state NC medicine drives; it must at least spawn as a guard.</summary>
    [Test]
    public async Task DebtorSpawnsAsTarget()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerVlad", coords);
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveNeutral", coords.Offset(new System.Numerics.Vector2(8, 0)));
            var (worker, profile) = SpawnWorker(coords, 900020);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerVlad", 0, "NCFixerVladDebtor"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            var debtor = missions.GetMissionEntities(profile).First();
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(debtor).EntityPrototype?.ID, Is.EqualTo("NCMobHumanDebtor"));
            Assert.That(SEntMan.HasComponent<NCMissionGuardComponent>(debtor), "The debtor fights back when beaten.");
        });
    }

    /// <summary>Guards warn an unarmed visitor and turn hostile, as a group, on someone holding a weapon.</summary>
    [Test]
    public async Task GuardsWarnFirstAndFightArmedVisitors()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var hands = Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid visitor = default;
        EntityUid armed = default;
        List<EntityUid> guards = new();

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerInsider", coords);
            SEntMan.SpawnEntity("NCFixerDeadDropInsider", coords.Offset(new System.Numerics.Vector2(1, 0)));
            SEntMan.SpawnEntity("NCMissionMarkerObjectiveTygerClaws", coords.Offset(new System.Numerics.Vector2(10, 0)));
            var (worker, profile) = SpawnWorker(coords, 900003);
            reputation.AddReputation(profile, 10);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerInsider", 0, "NCFixerInsiderTygerNames"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            guards = missions.GetMissionEntities(profile).Where(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid)).ToList();
            Assert.That(guards, Is.Not.Empty);

            var post = SEntMan.GetComponent<TransformComponent>(guards[0]).Coordinates;
            (visitor, _) = SpawnWorker(post.Offset(new System.Numerics.Vector2(0, 3)), 900004);
            (armed, _) = SpawnWorker(post.Offset(new System.Numerics.Vector2(0, -3)), 900005);
            var gun = SEntMan.SpawnEntity("NCWeaponPistolStreetmaster", SEntMan.GetComponent<TransformComponent>(armed).Coordinates);
            Assert.That(hands.TryPickupAnyHand(armed, gun), Is.True);
        });

        await Pair.RunSeconds(1);

        await Server.WaitAssertion(() =>
        {
            var guard = SEntMan.GetComponent<NCMissionGuardComponent>(guards[0]);
            Assert.That(guard.Warned.ContainsKey(visitor), "The unarmed visitor is warned.");
            Assert.That(guard.Hostile.Contains(visitor), Is.False, "Not attacked yet.");
            foreach (var uid in guards)
                Assert.That(SEntMan.GetComponent<NCMissionGuardComponent>(uid).Hostile.Contains(armed), "The whole group turns on the armed visitor.");
        });
    }

    /// <summary>Every defender of every encounter spawns with its gear.</summary>
    [Test]
    public async Task EncounterMobsSpawn()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            foreach (var encounter in SProtoMan.EnumeratePrototypes<NCFixerEncounterPrototype>())
            {
                foreach (var entry in encounter.Entries)
                {
                    var mob = SEntMan.SpawnEntity(entry.Mob, map.GridCoords);
                    Assert.That(SEntMan.HasComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(mob), $"{encounter.ID}: {entry.Mob}");
                    SEntMan.DeleteEntity(mob);
                }
            }
        });
    }

    /// <summary>Recon completes only once someone has stood at every point of the gang's ground.</summary>
    [Test]
    public async Task ReconNeedsEveryPoint()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var map = await Pair.CreateTestMap();
        var points = new List<EntityUid>();
        EntityUid scout = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerTabitha", coords);
            for (var i = 0; i < 3; i++)
                points.Add(SEntMan.SpawnEntity("NCMissionMarkerObjectiveBozos", coords.Offset(new System.Numerics.Vector2(8 + i * 6, 0))));
            (scout, profile) = SpawnWorker(coords, 900030);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerTabitha", 0, "NCFixerTabithaTurfBozos"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, scout), Is.True);
            transform.SetCoordinates(scout, SEntMan.GetComponent<TransformComponent>(points[0]).Coordinates);
            transform.SetCoordinates(SpawnWorker(coords, 900031).Worker, SEntMan.GetComponent<TransformComponent>(points[1]).Coordinates);
        });

        await Pair.RunSeconds(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.Active), "One point is still unscouted.");
            transform.SetCoordinates(scout, SEntMan.GetComponent<TransformComponent>(points[2]).Coordinates);
        });

        await Pair.RunSeconds(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out var status), Is.True);
            Assert.That(status, Is.EqualTo(NCFixerMissionStatus.AwaitingPayment), "All three points scouted.");
        });
    }

    /// <summary>
    /// The Militech operations board takes only Militech staff, and a finished job funds the field office and HQ
    /// instead of paying the taker.
    /// </summary>
    [Test]
    public async Task EmployerBoardFundsTheCorporation()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var bank = Server.System<Content.Server._NC.Bank.NCBankSystem>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);
        bank.TryGetDepartmentAccount("Militech", out var office);
        bank.TryGetDepartmentAccount("MilitechHQ", out var hq);
        var officeStart = office.Balance;
        var hqStart = hq.Balance;
        var map = await Pair.CreateTestMap();
        var points = new List<EntityUid>();
        EntityUid scout = default;
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerMilitechOps", coords);
            for (var i = 0; i < 3; i++)
                points.Add(SEntMan.SpawnEntity("NCMissionMarkerObjectiveArasakaRemnant", coords.Offset(new System.Numerics.Vector2(8 + i * 6, 0))));
            (scout, profile) = SpawnWorker(coords, 900040);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerMilitechOps", 0, "NCMilitechContractVaultRecon"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, scout), Is.False, "Only Militech staff take board jobs.");

            var minds = Server.System<SharedMindSystem>();
            Assert.That(minds.TryGetMind(scout, out var mindId, out _), Is.True);
            Server.System<Content.Shared.Roles.SharedRoleSystem>().MindAddJobRole(mindId, jobPrototype: "MilitechOperative");
            Assert.That(missions.TryAccept(fixer, 0, scout), Is.True);
        });

        foreach (var point in points)
        {
            await Server.WaitPost(() => transform.SetCoordinates(scout, SEntMan.GetComponent<TransformComponent>(point).Coordinates));
            await Pair.RunSeconds(2);
        }

        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out _), Is.False, "Paid on completion, nothing to collect.");
            Assert.That(office.Balance, Is.EqualTo(officeStart + 6000), "Half to the field office.");
            Assert.That(hq.Balance, Is.EqualTo(hqStart + 6000), "Half to HQ.");
        });
    }

    /// <summary>A peace offering job fails the moment the gang turns hostile.</summary>
    [Test]
    public async Task TruceFailsWhenTheGangTurns()
    {
        var missions = Server.System<NCFixerMissionSystem>();
        var reputation = Server.System<NCStreetReputationSystem>();
        var map = await Pair.CreateTestMap();
        var profile = 0;

        await Server.WaitAssertion(() =>
        {
            var coords = map.GridCoords;
            var fixerUid = SEntMan.SpawnEntity("NCFixerTabitha", coords);
            SEntMan.SpawnEntity("NCMissionMarkerDeadDropValentinos", coords.Offset(new System.Numerics.Vector2(10, 0)));
            var (worker, p) = SpawnWorker(coords, 900040);
            profile = p;
            reputation.AddReputation(profile, 10);
            var fixer = new Entity<NCFixerComponent>(fixerUid, SEntMan.GetComponent<NCFixerComponent>(fixerUid));
            Assert.That(missions.TrySetOffer("NCFixerTabitha", 0, "NCFixerTabithaTruceValentinos"), Is.True);
            Assert.That(missions.TryAccept(fixer, 0, worker), Is.True);
            var guard = missions.GetMissionEntities(profile).First(uid => SEntMan.HasComponent<NCMissionGuardComponent>(uid));
            SEntMan.GetComponent<NCMissionGuardComponent>(guard).Hostile.Add(worker);
        });

        await Pair.RunSeconds(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(missions.TryGetActiveMission(profile, out _, out _), Is.False, "The truce is off.");
            Assert.That(reputation.GetReputation(profile), Is.EqualTo(10 - 6));
        });
    }

    /// <summary>Money wired to a fee-free shell company arrives whole and the payer pays no fee.</summary>
    [Test]
    public async Task ShellCompanyTransfersCarryNoFee()
    {
        var bank = Server.System<Content.Server._NC.Bank.NCBankSystem>();
        await PoolManager.WaitUntil(Server, () => bank.Ready);
        bank.TryGetDepartmentAccount("NCPD", out var ncpd);
        bank.TryGetDepartmentAccount("NCFixerVanceShell", out var shell);
        var ncpdStart = ncpd.Balance;
        var shellStart = shell.Balance;

        Content.Server._NC.Bank.NCBankTransferResult? result = null;
        await Server.WaitPost(async () => result = await bank.TryTransferAsync(ncpd, shell.Number, 1000, null));
        await PoolManager.WaitUntil(Server, () => result != null);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(Content.Server._NC.Bank.NCBankTransferResult.Success));
            Assert.That(ncpd.Balance, Is.EqualTo(ncpdStart - 1000), "No fee on the way to the shell company.");
            Assert.That(shell.Balance, Is.EqualTo(shellStart + 1000));
        });
    }
}
