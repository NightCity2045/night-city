// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Atmos.EntitySystems;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Projectiles;
using Content.Shared.Trigger.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Reflection;

namespace Content.IntegrationTests.Tests._NC.Medical;

public sealed partial class NCBorgCoreTest
{
    [SidedDependency(Side.Server)] private AtmosphereSystem _acceptanceAtmos = default!;
    [SidedDependency(Side.Server)] private SharedInteractionSystem _acceptanceInteraction = default!;
    [SidedDependency(Side.Server)] private InventorySystem _acceptanceInventory = default!;
    [SidedDependency(Side.Server)] private SharedGunSystem _acceptanceGuns = default!;
    [SidedDependency(Side.Server)] private SharedTransformSystem _acceptanceTransform = default!;
    [SidedDependency(Side.Server)] private NCCyberwareAbilitySystem _acceptanceAbilities = default!;
    [SidedDependency(Side.Server)] private TriggerSystem _acceptanceTriggers = default!;
    [SidedDependency(Side.Server)] private NCCyberwareAcceptanceImpactSystem _acceptanceImpacts = default!;

    [TestCase(false), TestCase(true)]
    public async Task HeldRestorativeSyringeUsesRealMetabolismInHumanAndBiopod(bool biopod)
    {
        EntityUid body = default;
        EntityUid syringe = default;
        EntityUid brain = default;
        float before = 0;
        await Server.WaitAssertion(() =>
        {
            var (patient, user, core, _) = Prepared();
            SupplyAir(patient);
            body = patient;
            if (biopod)
            {
                Assert.That(_borg.TryConvert(core, user, patient), Is.True);
                Assert.That(_hands.TryDrop(user, core), Is.True);
                body = core;
            }
            else
                Assert.That(_hands.TryDrop(user, core), Is.True);
            _drugs.ResetBody(body);
            _scheduler.ProcessNow(body, TimeSpan.Zero);
            brain = Organ(body, "Brain");
            SComp<NCHumanityComponent>(brain).State.Current = 50;
            before = 50;
            syringe = SpawnAt("NCSyringeNeuralRestorative", user);
            Assert.That(_hands.TryPickupAnyHand(user, syringe), Is.True);
            _acceptanceInteraction.UserInteraction(user, SComp<TransformComponent>(body).Coordinates, body, altInteract: false);
        });
        // Includes injector DoAfter, upstream metabolism and the normal NC medical scheduler.
        await Pair.RunTicksSync(900);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_humanity.TryGetStatus(body, out var after), Is.True);
            Assert.That(after.Current, Is.GreaterThan(before).And.LessThanOrEqualTo(after.Maximum));
            Assert.That(Organ(body, "Brain"), Is.EqualTo(brain));
            Assert.That(SComp<NCPharmacologyComponent>(body).ActiveDrugs,
                Contains.Key((ProtoId<NCDrugProfilePrototype>) "NCDrugNeuralRestorative"));
            Assert.That(_solutions.TryGetSolution(syringe, SComp<InjectorComponent>(syringe).SolutionName, out _, out var remaining), Is.True);
            Assert.That(remaining!.Volume.Float(), Is.LessThan(10f));
            Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.False);
            TestContext.Out.WriteLine($"restorative biopod={biopod}, H={before}->{after.Current}, syringe={remaining.Volume}");
            WriteAcceptanceReport($"restorative-biopod-{biopod}.json", new
            {
                biopod, simulatedSeconds = 15, humanityBefore = before,
                humanityAfter = after.Current, humanityMaximum = after.Maximum,
                syringeRemaining = remaining.Volume.Float(),
                perfusion = SComp<NCCirculationComponent>(body).Perfusion,
            });
        });
    }

    /// <summary>A stationary, scripted physical firefight, not a claim about human tactics or population capacity.</summary>
    [TestCase(false), TestCase(true)]
    public async Task HeavyCombatBorgVersusPreparedMixedTeamUsesPhysicalGuns(bool teamUsesEmp)
    {
        EntityUid borg = default;
        EntityUid borgGun = default;
        EntityUid frameCell = default;
        var team = new List<(EntityUid Body, EntityUid Gun)>();
        var acceptedBorg = 0;
        var acceptedTeam = 0;
        var frameTimes = new List<double>();
        var impacts = _acceptanceImpacts;
        var process = Process.GetCurrentProcess();
        await Server.WaitAssertion(() =>
        {
            var (patient, user, core, _) = Prepared();
            SupplyAir(patient);
            Assert.That(_borg.TryConvert(core, user, patient), Is.True);
            var shell = SpawnAt("NCBorgStandardShell", user);
            frameCell = InstallCell(Organ(shell, "Thorax"), 2000);
            _cyberware.ResetPowerClock(shell);
            Assert.That(_borg.TryDock(core, user, shell), Is.True);
            borg = core;
            _drugs.ResetBody(borg);
            _scheduler.ProcessNow(borg, TimeSpan.Zero);
            EquipCombatArmor(borg);
            var burst = InstallAcceptanceModule(borg, "NCSandevistanImplant", "NCSlotMotorBurst");
            Assert.That(_cyberware.TryCalibrateCyberware(burst, NCActionResolutionId.New(), out _), Is.True);
            _acceptanceAbilities.RefreshBody(borg);
            Assert.That(_acceptanceAbilities.TryToggle(borg, burst), Is.True);
            var map = SComp<TransformComponent>(borg).MapID;
            Assert.That(_maps.TryGetMap(map, out var mapUid), Is.True);
            // Keep the operation's abandoned body, operator and socket out of the firing lanes.
            var offstage = new EntityCoordinates(mapUid!.Value, new Vector2(0, 12));
            if (SComp<BuckleComponent>(patient).BuckledTo is { } table)
                _acceptanceTransform.SetCoordinates(table, offstage);
            _acceptanceTransform.SetCoordinates(patient, offstage);
            _acceptanceTransform.SetCoordinates(user, offstage);
            _acceptanceTransform.SetCoordinates(shell, offstage);
            _acceptanceTransform.SetCoordinates(borg, new EntityCoordinates(mapUid!.Value, new Vector2(-4, 0)));
            borgGun = SpawnAt("WeaponRifleLecter", borg);
            Assert.That(_hands.TryPickupAnyHand(borg, borgGun), Is.True);
            PrepareRifle(borg, borgGun);
            for (var i = 0; i < 3; i++)
            {
                var human = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(4, (i - 1) * 2), map));
                EquipCombatArmor(human);
                if (i == 1)
                {
                    NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, human, 1000);
                    var stabilizer = InstallAcceptanceModule(human, "NCKerenzikovImplant", "NCSlotMotorStabilizer");
                    Assert.That(_cyberware.TryCalibrateCyberware(stabilizer, NCActionResolutionId.New(), out _), Is.True);
                    _acceptanceAbilities.RefreshBody(human);
                    Assert.That(_acceptanceAbilities.TryToggle(human, stabilizer), Is.True);
                }
                var gun = SpawnAt("WeaponRifleLecter", human);
                Assert.That(_hands.TryPickupAnyHand(human, gun), Is.True);
                PrepareRifle(human, gun);
                team.Add((human, gun));
            }
            impacts.BeginCapture(team.Select(member => member.Body).Append(borg));
            if (teamUsesEmp)
            {
                var grenade = SpawnAt("EmpGrenade", borg);
                Assert.That(_acceptanceTriggers.Trigger(grenade, team[2].Body, "trigger", predicted: false), Is.True);
            }
        });
        var cpuStart = process.TotalProcessorTime;
        for (var step = 0; step < 12; step++)
        {
            await Server.WaitPost(() =>
            {
                var target = team[step % team.Count].Body;
                if (_acceptanceGuns.AttemptShoot(borg, (borgGun, SComp<GunComponent>(borgGun)), SComp<TransformComponent>(target).Coordinates, target))
                    acceptedBorg++;
                foreach (var member in team)
                    if (_acceptanceGuns.AttemptShoot(member.Body, (member.Gun, SComp<GunComponent>(member.Gun)), SComp<TransformComponent>(borg).Coordinates, borg))
                        acceptedTeam++;
            });
            var clock = Stopwatch.StartNew();
            await Pair.RunTicksSync(30);
            frameTimes.Add(clock.Elapsed.TotalMilliseconds / 30);
        }
        await Server.WaitAssertion(() =>
        {
            impacts.EndCapture();
            var wounds = team.Select(member => member.Body).Append(borg)
                .Sum(uid => SEntMan.TryGetComponent<NCWoundContainerComponent>(uid, out var state) ? state.Wounds.Count : 0);
            var physicalHits = team.Select(member => member.Body).Append(borg)
                .Sum(uid => impacts.Counts.GetValueOrDefault(uid));
            frameTimes.Sort();
            var report = new
            {
                scenario = "stationary-heavy-borg-vs-three-rifle-team", teamUsesEmp,
                simulatedSeconds = 6, actors = 4, tickBatches = frameTimes.Count,
                acceptedBorg, acceptedTeam, physicalHits, wounds,
                borgHits = impacts.Counts.GetValueOrDefault(borg),
                teamHits = team.Select(member => impacts.Counts.GetValueOrDefault(member.Body)).ToArray(),
                borgWounds = WoundCount(borg),
                teamWounds = team.Select(member => WoundCount(member.Body)).ToArray(),
                borgCharge = _battery.GetCharge(frameCell),
                borg = SComp<NCPhysiologyComponent>(borg).Condition.ToString(),
                team = team.Select(member => SComp<NCPhysiologyComponent>(member.Body).Condition.ToString()).ToArray(),
                tickBatchMeanMs = frameTimes.Average(), tickBatchP95Ms = frameTimes[^1],
                harnessCpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
                harnessWorkingSetBytes = process.WorkingSet64,
                limitations = "One scripted stationary exchange; timings include server and headless client harness. Not player balance or server capacity. A native EMP grenade is triggered at the Borg position; throwing tactics are not measured."
            };
            WriteAcceptanceReport($"combat-emp-{teamUsesEmp}.json", report);
            Assert.That(acceptedTeam, Is.GreaterThan(0));
            if (!teamUsesEmp)
                Assert.That(acceptedBorg, Is.GreaterThan(0));
            Assert.That(physicalHits, Is.GreaterThan(0),
                "Native projectile collisions must reach the combatants, including hits stopped by armor.");
        });
    }

    private int WoundCount(EntityUid body) =>
        SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var state) ? state.Wounds.Count : 0;

    private void PrepareRifle(EntityUid user, EntityUid gun)
    {
        _acceptanceGuns.UseChambered(gun, SComp<ChamberMagazineAmmoProviderComponent>(gun), user);
        Assert.That(_acceptanceGuns.GetChamberEntity(gun), Is.Not.Null);
    }

    private static void WriteAcceptanceReport(string file, object report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        var directory = Environment.GetEnvironmentVariable("NC_CYBERWARE_ACCEPTANCE_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
            return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), json);
    }

    private void SupplyAir(EntityUid body)
    {
        Assert.That(_maps.TryGetMap(SComp<TransformComponent>(body).MapID, out var map), Is.True);
        var mixture = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        mixture.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
        mixture.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
        _acceptanceAtmos.SetMapAtmosphere(map!.Value, false, mixture);
    }

    private void EquipCombatArmor(EntityUid body)
    {
        Assert.That(_acceptanceInventory.TryEquip(body, SpawnAt("ClothingOuterArmorHeavy", body), "outerClothing"), Is.True);
        Assert.That(_acceptanceInventory.TryEquip(body, SpawnAt("ClothingHeadHelmetBasic", body), "head"), Is.True);
    }

    private EntityUid InstallAcceptanceModule(EntityUid body, EntProtoId prototype, ProtoId<NCCyberwareSlotPrototype> slot)
    {
        var module = SpawnAt(prototype, body);
        Assert.That(_cyberware.TryInstall(new(NCActionResolutionId.New(), body, Organ(body, "Head"), module, slot, true, true)).Accepted, Is.True);
        return module;
    }
}

/// <summary>Passive fixture observer; it counts native collisions without changing projectiles or damage.</summary>
[Reflect(false)]
public sealed partial class NCCyberwareAcceptanceImpactSystem : EntitySystem
{
    private readonly HashSet<EntityUid> _targets = [];
    public readonly Dictionary<EntityUid, int> Counts = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProjectileComponent, ProjectileHitEvent>(OnHit);
    }

    public void BeginCapture(IEnumerable<EntityUid> targets)
    {
        _targets.Clear();
        _targets.UnionWith(targets);
        Counts.Clear();
    }

    public void EndCapture() => _targets.Clear();

    private void OnHit(Entity<ProjectileComponent> projectile, ref ProjectileHitEvent args)
    {
        if (_targets.Contains(args.Target))
            Counts[args.Target] = Counts.GetValueOrDefault(args.Target) + 1;
    }
}
