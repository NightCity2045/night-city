// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Function;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Gate 0's repeatable scene uses existing physical replacements and public mutation boundaries.
/// Diagnostic reports describe missing integrations; passing a report is not acceptance of Full Borg.
/// </summary>
[TestFixture]
[Category("NCCyberwareGate0")]
public sealed class NCCyberwareIntegrationContractTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCBodyFunctionSystem _functions = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private Content.Server._NC.Medical.Humanity.NCHumanitySystem _humanity = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _powerSlots = default!;
    [SidedDependency(Side.Server)] private PowerCellSystem _cells = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void CyberarmPowerLossAndServiceReachOnlyTheAffectedGrip()
    {
        var body = CreateScene("ArmLeft");
        var arm = GetOrgan(body, "ArmLeft");
        var function = SComp<NCBodyFunctionComponent>(body);
        var initialGrip = function.LeftGrip;
        var initialOtherGrip = function.RightGrip;
        var initialMovement = function.Movement;
        Assert.That(initialGrip, Is.GreaterThan(0f));

        DrainPower(arm);
        Assert.Multiple(() =>
        {
            Assert.That(function.LeftGrip, Is.Zero.Within(0.001f));
            Assert.That(function.RightGrip, Is.EqualTo(initialOtherGrip).Within(0.001f));
            Assert.That(function.Movement, Is.EqualTo(initialMovement).Within(0.001f));
            Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.False);
        });

        Service(arm);
        Assert.That(function.LeftGrip, Is.EqualTo(initialGrip).Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CyberopticsPowerLossAndServiceReachVision()
    {
        var body = CreateScene("Eyes");
        var eyes = GetOrgan(body, "Eyes");
        var function = SComp<NCBodyFunctionComponent>(body);
        var initialVision = function.Vision;
        Assert.That(initialVision, Is.GreaterThan(0f));

        DrainPower(eyes);
        Assert.That(function.Vision, Is.Zero.Within(0.001f));
        Service(eyes);
        Assert.That(function.Vision, Is.EqualTo(initialVision).Within(0.001f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ReportCyberheartPowerLossIntegration()
    {
        var body = CreateScene("Heart");
        var heart = GetOrgan(body, "Heart");
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        var initialOutput = SComp<NCCirculationComponent>(body).CardiacOutput;
        Assert.That(initialOutput, Is.GreaterThan(0f));

        DrainPower(heart);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        var output = SComp<NCCirculationComponent>(body).CardiacOutput;
        Assert.That(float.IsFinite(output), Is.True);

        Assert.That(output, Is.Zero.Within(0.001f));
        // Gate 2 now makes the cardiac power coupling an acceptance condition.
        Report("cyberheart-power-loss", new
        {
            Kind = "integration-probe",
            InitialCardiacOutput = initialOutput,
            Powered = SComp<NCCyberwareComponent>(heart).Operational,
            ExpectedCardiacOutput = 0f,
            ActualCardiacOutput = output,
            MeetsTarget = MathF.Abs(output) <= 0.001f,
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ReportOrdinaryBrainDetachmentBoundary()
    {
        var body = SSpawn(HumanMob);
        var originalBrain = GetOrgan(body, "Brain");
        var physiology = SComp<NCPhysiologyComponent>(body);
        Assert.That(physiology.Irreversible, Is.False);
        Assert.That(_detachment.TryDetachOrgan(body, "Brain", out var detached), Is.True);
        Assert.That(detached, Is.EqualTo(originalBrain));
        var irreversibleAfterDetach = physiology.Irreversible;

        Assert.That(_detachment.TryAttachOrgan(body, "Head", detached), Is.True);
        Assert.That(GetOrgan(body, "Brain"), Is.EqualTo(originalBrain));
        Report("ordinary-brain-detachment", new
        {
            Kind = "integration-probe",
            SamePhysicalBrain = true,
            IrreversibleAfterDetach = irreversibleAfterDetach,
            IrreversibleAfterReattach = physiology.Irreversible,
            Scope = "ordinary unsupported detachment; not a biopod transfer",
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CaptureExplicitMedicalBoundaryBaseline()
    {
        const int warmupIterations = 5;
        const int measuredIterations = 100;
        var body = CreateScene();
        var modules = _cyberware.CollectCyberware(body).ToArray();
        Assert.That(modules, Has.Length.EqualTo(4));
        for (var i = 0; i < warmupIterations; i++)
            Assert.That(AdvanceScene(body, modules), Is.True);

        var before = _functions.GetRuntimeCounters();
        var advanced = true;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < measuredIterations; i++)
            advanced &= AdvanceScene(body, modules);
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var after = _functions.GetRuntimeCounters();

        Assert.Multiple(() =>
        {
            Assert.That(advanced, Is.True);
            Assert.That(modules.All(module => SComp<NCCyberwareComponent>(module).Operational), Is.True);
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition, Is.EqualTo(NCPhysiologicalCondition.Stable));
            Assert.That(_cyberware.CollectCyberware(body), Has.Count.EqualTo(4));
        });
        Report("explicit-medical-boundaries", new
        {
            Kind = "workload-baseline",
            Subjects = 1,
            CyberwareModules = modules.Length,
            WarmupIterations = warmupIterations,
            MeasuredIterations = measuredIterations,
            BodyPowerAdvances = measuredIterations,
            ModulesInScene = modules.Length,
            SimulatedSecondsPerIteration = 1,
            ElapsedMilliseconds = milliseconds,
            AllocatedBytesOnServerThread = allocatedBytes,
            FunctionRefreshes = after.Refreshes - before.Refreshes,
            SuppressedPhysiologyRefreshes =
                after.SuppressedPhysiologyRefreshes - before.SuppressedPhysiologyRefreshes,
            Scope = "explicit API calls; excludes setup, real server ticks, networking and database IO",
        });
    }

    private EntityUid CreateScene(ProtoId<OrganCategoryPrototype>? replacement = null)
    {
        // Restore supplies a reproducible starting graph, not a shortcut for a player's surgical procedure.
        // Existing cyberware tests use the same semantic restore boundary for anatomical replacements.
        var template = SSpawn(HumanMob);
        NCCyberwareTestPower.Install(SEntMan, _cyberware, _powerSlots, _battery, template);
        var snapshot = _persistence.SerializeMedicalState(template);
        Assert.That(snapshot, Is.Not.Null);
        // The replacements below deliberately construct the pre-exterior/service fixture format.
        snapshot!.Version = NCMedicalSnapshotVersions.CyberwareAbilities;
        if (replacement == null || replacement.Value.Id == "ArmLeft")
            ReplaceOrgan(snapshot!, "ArmLeft", "NCOrganHumanCyberArmLeft", "NCIndustrialCyberArm");
        if (replacement == null || replacement.Value.Id == "Eyes")
            ReplaceOrgan(snapshot!, "Eyes", "NCOrganHumanCyberEyes", "NCCyberopticsMk1");
        if (replacement == null || replacement.Value.Id == "Heart")
            ReplaceOrgan(snapshot!, "Heart", "NCOrganHumanCyberHeart", "NCCyberheartMk1");
        foreach (var module in snapshot.CyberwareModules)
        {
            module.Exterior = null;
            module.Service = null;
        }
        // The fixture replaces its template; retaining it would duplicate the bus and cell serials.
        SEntMan.DeleteEntity(template);
        var body = SSpawn(HumanMob);
        Assert.That(_humanity.TryPrepareLegacyState(body, snapshot!), Is.True);
        Assert.That(_persistence.RestoreMedicalState(body, snapshot!), Is.True);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        return body;
    }

    private void ReplaceOrgan(
        NCMedicalStateSnapshot snapshot,
        ProtoId<OrganCategoryPrototype> category,
        EntProtoId prototype,
        ProtoId<NCCyberwarePrototype> profileId)
    {
        var profile = SProtoMan.Index(profileId);
        var state = snapshot.Organs[category];
        state.EntityPrototype = prototype.Id;
        state.CyberwareIdentity = prototype.Id;
        state.Integrity!.MaxIntegrity = profile.MaximumIntegrity;
        state.Integrity.CurrentIntegrity = profile.MaximumIntegrity;
        state.Integrity.FunctionalEfficiency = 1f;
        state.Integrity.Necrosis = 0f;
        state.Integrity.Material = NCAnatomicalMaterialKind.Mechanical;
        // Organic eyes have no NCNerve snapshot, while cyberoptics explicitly declare the interface.
        // Restore requires an exact match between the snapshot sections and the replacement prototype.
        if (profile.NeuralRequirement != NCCyberwareNeuralRequirement.None)
        {
            state.Nerve = new NCNerveStateSnapshot
            {
                NeuralIntegrity = 1f,
                Sensitivity = 1f,
                MotorFunction = 1f,
                CyberprostheticInterface = true,
            };
        }
        state.Cyberware = new NCCyberwareStateSnapshot
        {
            EntityPrototype = prototype.Id,
            Profile = profileId,
            StableInstanceId = Guid.NewGuid(),
            CurrentIntegrity = profile.MaximumIntegrity,
            MaximumIntegrity = profile.MaximumIntegrity,
            CurrentPower = 0f,
            NeuralIntegrity = 1f,
            NeuralState = profile.NeuralRequirement == NCCyberwareNeuralRequirement.None
                ? NCCyberwareNeuralState.NotApplicable
                : NCCyberwareNeuralState.Linked,
            Calibrated = true,
        };
        state.Cyberware.Assembly = _cyberware.CreateDefaultAssemblySnapshot(profileId,
            state.Cyberware.StableInstanceId);
    }

    private void DrainPower(EntityUid module)
    {
        var body = SComp<OrganComponent>(module).Body!.Value;
        _battery.SetCharge(GetBodyCell(body), 0f);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(module).Operational, Is.False);
    }

    private void Service(EntityUid module)
    {
        // Explicit fixture supply represents a real external recharge, not a repair reagent.
        var body = SComp<OrganComponent>(module).Body!.Value;
        _battery.ChangeCharge(GetBodyCell(body), 10000f);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.That(SComp<NCCyberwareComponent>(module).Operational, Is.True);
    }

    private EntityUid GetBodyCell(EntityUid body)
    {
        var holder = _cyberware.CollectCyberware(body).Single(uid => SEntMan.HasComponent<NCCyberwarePowerBusComponent>(uid));
        Assert.That(_cells.TryGetBatteryFromSlot(holder, out var battery), Is.True);
        return battery!.Value.Owner;
    }

    private bool AdvanceScene(EntityUid body, EntityUid[] modules)
    {
        var observedAt = SComp<NCCyberwarePowerStateComponent>(body).LastAdvancedAt + TimeSpan.FromSeconds(1);
        return _cyberware.TryAdvanceBodyPower(body, observedAt) && _scheduler.ProcessNow(body, TimeSpan.FromSeconds(1));
    }

    private EntityUid GetOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category)
    {
        Assert.That(_anatomy.TryGetOrgan(body, category, out var organ), Is.True);
        return organ;
    }

    private static void Report(string scenario, object data)
    {
        TestContext.Out.WriteLine("NC_CYBERWARE_GATE0 " +
                                  JsonSerializer.Serialize(new { Scenario = scenario, Data = data }));
    }
}
