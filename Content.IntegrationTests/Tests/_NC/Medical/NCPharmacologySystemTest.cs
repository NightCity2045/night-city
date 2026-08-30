// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCPharmacologySystem))]
public sealed class NCPharmacologySystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly ProtoId<ReagentPrototype> CyberwareCoolantAgent = "NCCyberwareCoolantAgent";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void ProfilesInteractionsAndReagentBridgesAreSemantic()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCPharmacologyProfilePrototype>().ToList();
        var drugs = _prototypes.EnumeratePrototypes<NCDrugProfilePrototype>()
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();

        Assert.That(profiles.Count(candidate => candidate.DefaultForHumans), Is.EqualTo(1));
        Assert.That(profiles, Is.All.Matches<NCPharmacologyProfilePrototype>(NCPharmacologySystem.ValidateProfile));
        Assert.That(drugs, Has.Count.GreaterThanOrEqualTo(14));
        Assert.That(drugs, Is.All.Matches<NCDrugProfilePrototype>(NCPharmacologySystem.ValidateDrug));
        Assert.That(
            drugs.Select(drug => drug.Category),
            Is.SupersetOf(new[]
            {
                NCDrugCategory.Analgesic,
                NCDrugCategory.Anesthetic,
                NCDrugCategory.Sedative,
                NCDrugCategory.Stimulant,
                NCDrugCategory.Antibiotic,
                NCDrugCategory.Procoagulant,
                NCDrugCategory.Anticoagulant,
                NCDrugCategory.Vasopressor,
                NCDrugCategory.Vasodilator,
                NCDrugCategory.Antiarrhythmic,
                NCDrugCategory.RespiratoryStimulant,
                NCDrugCategory.Antitoxin,
                NCDrugCategory.Immunosuppressant,
                NCDrugCategory.SyntheticOxygenCarrier,
            }));

        var humanProfile = profiles.Single(candidate => candidate.DefaultForHumans);

        foreach (var drug in drugs)
        {
            foreach (var (otherId, rule) in drug.Interactions)
            {
                var other = _prototypes.Index(otherId);
                Assert.That(other.Interactions.TryGetValue(drug.ID, out var reverse), Is.True, drug.ID);
                Assert.Multiple(() =>
                {
                    Assert.That(reverse!.TherapeuticMultiplier, Is.EqualTo(rule.TherapeuticMultiplier), drug.ID);
                    Assert.That(reverse.ToxicityMultiplier, Is.EqualTo(rule.ToxicityMultiplier), drug.ID);
                    Assert.That(rule.TherapeuticMultiplier, Is.LessThanOrEqualTo(humanProfile.MaximumInteractionMultiplier));
                    Assert.That(rule.ToxicityMultiplier, Is.LessThanOrEqualTo(humanProfile.MaximumInteractionMultiplier));
                });
            }
        }

        var bridged = _prototypes.EnumeratePrototypes<ReagentPrototype>()
            .Where(reagent => reagent.ID.StartsWith("NC", StringComparison.Ordinal))
            .Where(reagent => reagent.Metabolisms != null)
            .SelectMany(reagent => reagent.Metabolisms!.Metabolisms.Values)
            .SelectMany(entry => entry.Effects)
            .OfType<NCApplyMedicalDrug>()
            .Select(effect => effect.Profile)
            .ToHashSet();
        Assert.That(bridged, Is.EquivalentTo(drugs.Select(drug => (ProtoId<NCDrugProfilePrototype>) drug.ID)));

        var coolantBridge = _prototypes.Index(CyberwareCoolantAgent)
            .Metabolisms!.Metabolisms.Values
            .SelectMany(entry => entry.Effects)
            .OfType<NCApplyCyberwareServiceAgent>()
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(coolantBridge.UnitsPerReagent, Is.GreaterThan(0f));
            Assert.That(
                _prototypes.TryIndex<NCCyberwareRepairProfilePrototype>(coolantBridge.ServiceProfile, out _),
                Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AnalgesiaChangesPerceptionWithoutErasingTrauma()
    {
        var body = SSpawn(HumanMob);
        var wounds = SEntMan.EnsureComponent<NCWoundContainerComponent>(body);
        var woundId = Guid.NewGuid();
        wounds.Wounds.Add(woundId, new NCWoundRecord
        {
            WoundId = woundId,
            Prototype = "NCWoundSlash",
            TargetZone = "NCZoneArmLeft",
            RegionCategory = "ArmLeft",
            Pain = 100f,
            InitialPain = 100f,
        });

        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        var baseline = SComp<NCPhysiologyComponent>(body).TotalPain;

        var effect = new NCApplyMedicalDrug
        {
            Profile = "NCDrugTraumaAnalgesic",
        };
        var effectEvent = new EntityEffectEvent<NCApplyMedicalDrug>(effect, 6f, null);
        SEntMan.EventBus.RaiseLocalEvent(body, ref effectEvent);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(baseline, Is.EqualTo(100f));
            Assert.That(SComp<NCPhysiologyComponent>(body).TotalPain, Is.EqualTo(25f).Within(0.001f));
            Assert.That(wounds.Wounds[woundId].Pain, Is.EqualTo(100f));
            Assert.That(wounds.Wounds, Has.Count.EqualTo(1));
            Assert.That(_scheduler.GetRemainingDelay(body), Is.GreaterThan(TimeSpan.Zero));
            Assert.That(_scheduler.GetRemainingDelay(body), Is.LessThanOrEqualTo(TimeSpan.FromSeconds(1)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AnesthesiaReducesConsciousnessAndMotorControlWithoutDeletingAnatomy()
    {
        var body = SSpawn(HumanMob);
        var baseline = SComp<NCBodyFunctionComponent>(body).Movement;
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugSurgicalAnesthetic", 5f).Accepted, Is.True);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCBodyFunctionComponent>(body).Movement, Is.LessThan(baseline));
            Assert.That(
                SComp<NCPhysiologyComponent>(body).Consciousness,
                Is.GreaterThanOrEqualTo(NCConsciousnessState.Stuporous));
            Assert.That(SComp<BodyComponent>(body).Organs!.ContainedEntities, Is.Not.Empty);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LiveLiverAndKidneyFunctionControlClearance()
    {
        var healthy = SSpawn(HumanMob);
        var impaired = SSpawn(HumanMob);
        Assert.That(_pharmacology.ApplyDose(healthy, "NCDrugTraumaAnalgesic", 6f).Accepted, Is.True);
        Assert.That(_pharmacology.ApplyDose(impaired, "NCDrugTraumaAnalgesic", 6f).Accepted, Is.True);

        SetOrganFunction(impaired, NCOrganFunctionType.Metabolism, 0f);
        SetOrganFunction(impaired, NCOrganFunctionType.Filtration, 0f);
        Assert.That(_pharmacology.AdvanceBody(healthy, TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(_pharmacology.AdvanceBody(impaired, TimeSpan.FromSeconds(10)), Is.True);

        var healthyExposure = SComp<NCPharmacologyComponent>(healthy).ActiveDrugs["NCDrugTraumaAnalgesic"];
        var impairedExposure = SComp<NCPharmacologyComponent>(impaired).ActiveDrugs["NCDrugTraumaAnalgesic"];
        Assert.That(impairedExposure.Concentration, Is.GreaterThan(healthyExposure.Concentration));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void InteractionsAreIndependentOfAdministrationOrder()
    {
        var first = SSpawn(HumanMob);
        var second = SSpawn(HumanMob);
        _pharmacology.ApplyDose(first, "NCDrugSurgicalAnesthetic", 5f);
        _pharmacology.ApplyDose(first, "NCDrugSedative", 6f);
        _pharmacology.ApplyDose(second, "NCDrugSedative", 6f);
        _pharmacology.ApplyDose(second, "NCDrugSurgicalAnesthetic", 5f);

        Assert.That(_pharmacology.GetModifiers(first), Is.EqualTo(_pharmacology.GetModifiers(second)));

        var trace = SSpawn(HumanMob);
        var control = SSpawn(HumanMob);
        _pharmacology.ApplyDose(trace, "NCDrugSurgicalAnesthetic", 5f);
        _pharmacology.ApplyDose(trace, "NCDrugSedative", 0.1f);
        _pharmacology.ApplyDose(control, "NCDrugSurgicalAnesthetic", 5f);
        Assert.That(_pharmacology.GetModifiers(trace), Is.EqualTo(_pharmacology.GetModifiers(control)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AntitoxinAndOverdoseUseSystemicAndOrganAuthority()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_systemic.Apply(body, "NCToxinSystemicDamage", 20f).Accepted, Is.True);
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugAntitoxin", 5f).Accepted, Is.True);
        var toxinBefore = _systemic.GetBurden(body, NCSystemicChannel.Toxin);
        Assert.That(_pharmacology.AdvanceBody(body, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Toxin), Is.LessThan(toxinBefore));

        var liverBefore = GetOrganFunction(body, NCOrganFunctionType.Metabolism);
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugSurgicalAnesthetic", 30f).Accepted, Is.True);
        Assert.That(_pharmacology.AdvanceBody(body, TimeSpan.FromSeconds(60)), Is.True);
        var state = SComp<NCPharmacologyComponent>(body);
        Assert.Multiple(() =>
        {
            Assert.That(state.OverdoseBurden, Is.GreaterThan(0f));
            Assert.That(state.CachedModifiers.ShockContribution, Is.GreaterThan(0f));
            Assert.That(state.CachedModifiers.RhythmInstability, Is.GreaterThan(0f));
            Assert.That(state.CachedModifiers.MotorControlMultiplier, Is.LessThan(1f));
            Assert.That(GetOrganFunction(body, NCOrganFunctionType.Metabolism), Is.LessThan(liverBefore));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PersistenceFreezesExposureAndRejectsCorruptionTransactionally()
    {
        var source = SSpawn(HumanMob);
        Assert.That(_pharmacology.ApplyDose(source, "NCDrugBroadAntibiotic", 5f).Accepted, Is.True);
        Assert.That(
            _pharmacology.TryCapturePersistentState(source, _timing.CurTime, out var snapshot),
            Is.True);
        Assert.That(snapshot, Is.Not.Null);

        var restored = SSpawn(HumanMob);
        var runtimeAnchor = _timing.CurTime + TimeSpan.FromHours(12);
        Assert.That(
            _pharmacology.TryRestorePersistentState(restored, snapshot, runtimeAnchor),
            Is.True);
        var restoredExposure = SComp<NCPharmacologyComponent>(restored)
            .ActiveDrugs["NCDrugBroadAntibiotic"];
        Assert.Multiple(() =>
        {
            Assert.That(restoredExposure.Concentration, Is.EqualTo(5f));
            Assert.That(restoredExposure.LastAdministration, Is.EqualTo(runtimeAnchor));
        });

        snapshot!.ActiveDrugs[0].Concentration = float.NaN;
        Assert.That(
            _pharmacology.TryRestorePersistentState(restored, snapshot, runtimeAnchor),
            Is.False);
        Assert.That(
            SComp<NCPharmacologyComponent>(restored)
                .ActiveDrugs["NCDrugBroadAntibiotic"]
                .Concentration,
            Is.EqualTo(5f));
    }

    private void SetOrganFunction(EntityUid body, NCOrganFunctionType type, float capacity)
    {
        var organ = FindOrgan(body, type);
        SComp<NCOrganFunctionComponent>(organ).CurrentCapacity = capacity;
    }

    private float GetOrganFunction(EntityUid body, NCOrganFunctionType type)
    {
        var function = SComp<NCOrganFunctionComponent>(FindOrgan(body, type));
        return function.BaseCapacity > 0f ? function.CurrentCapacity / function.BaseCapacity : 0f;
    }

    private EntityUid FindOrgan(EntityUid body, NCOrganFunctionType type)
    {
        var organs = SComp<BodyComponent>(body).Organs!;
        return organs.ContainedEntities.Single(organ =>
            SEntMan.TryGetComponent<NCOrganFunctionComponent>(organ, out var function) &&
            function.FunctionType == type);
    }
}
