// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Surgery;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCSurgerySystem))]
public sealed class NCSurgerySystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly EntProtoId OperatingTable = "OperatingTable";
    private static readonly ProtoId<NCSurgeryProcedurePrototype> VascularRepair = "NCSurgeryVascularRepair";
    private static readonly ProtoId<NCSurgeryProcedurePrototype> DeepWoundTreatment = "NCSurgeryDeepWoundTreatment";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCSurgerySystem _surgery = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void SurgeryCatalogueIsExplicitDeterministicAndAcyclic()
    {
        var procedures = _prototypes.EnumeratePrototypes<NCSurgeryProcedurePrototype>().ToList();
        var expectedProcedures = new[]
        {
            "NCSurgeryDeepWoundTreatment",
            "NCSurgeryVascularRepair",
            "NCSurgeryFractureFixation",
            "NCSurgeryExposeOrgan",
            "NCSurgeryOrganRepair",
            "NCSurgeryWoundIrrigation",
            "NCSurgeryForeignBodyExtraction",
            "NCSurgeryTendonRepair",
            "NCSurgeryPeripheralNerveRepair",
            "NCSurgeryCranialDecompression",
            "NCSurgeryBrainRepair",
            "NCSurgeryEyeRepair",
            "NCSurgeryOrganRemoval",
            "NCSurgeryEyeRemoval",
            "NCSurgeryOrganTransplant",
            "NCSurgeryEyeTransplant",
            "NCSurgeryLimbAmputation",
            "NCSurgeryLimbReattachment",
            "NCSurgeryRegionalAutopsy",
            "NCSurgeryCyberwareInstallation",
            "NCSurgeryCyberwareRemoval",
        };
        Assert.That(procedures.Select(procedure => procedure.ID), Is.SupersetOf(expectedProcedures));
        Assert.That(_prototypes.EnumeratePrototypes<NCSurgeryStepPrototype>(), Is.Not.Empty);

        foreach (var procedure in procedures)
        {
            Assert.Multiple(() =>
            {
                Assert.That(procedure.CompatibleBodyProfiles, Is.Not.Empty, procedure.ID);
                Assert.That(procedure.StartingAccessDepths, Is.Not.Empty, procedure.ID);
                Assert.That(procedure.Steps, Is.Not.Empty, procedure.ID);
                Assert.That(procedure.Steps, Is.Unique, procedure.ID);

                if (procedure.CyberwareOperation != NCSurgeryCyberwareOperation.None)
                {
                    Assert.That(procedure.CompatibleCyberwareProfiles, Is.Not.Empty, procedure.ID);
                    Assert.That(procedure.RequiresGraft,
                        Is.EqualTo(procedure.CyberwareOperation == NCSurgeryCyberwareOperation.Install),
                        procedure.ID);
                }

                // This catalogue intentionally has no awake live-patient path. Every such procedure must
                // be gated by all three server-authoritative pharmacological effects.
                if (!procedure.Patient.AllowedConditions.Contains(NCPhysiologicalCondition.Dead))
                {
                    Assert.That(procedure.Patient.MinimumAnalgesia, Is.GreaterThan(0f), procedure.ID);
                    Assert.That(procedure.Patient.MinimumSedation, Is.GreaterThan(0f), procedure.ID);
                    Assert.That(procedure.Patient.MinimumAnesthesia, Is.GreaterThan(0f), procedure.ID);
                }
            });

            foreach (var bodyProfile in procedure.CompatibleBodyProfiles)
                Assert.That(_prototypes.HasIndex(bodyProfile), Is.True, $"{procedure.ID}: {bodyProfile}");
            foreach (var authorization in procedure.RequiredAuthorizations)
                Assert.That(_prototypes.HasIndex(authorization), Is.True, $"{procedure.ID}: {authorization}");
            foreach (var cyberware in procedure.CompatibleCyberwareProfiles)
                Assert.That(_prototypes.HasIndex(cyberware), Is.True, $"{procedure.ID}: {cyberware}");
            ValidateFacilityCapabilities(procedure.ID, procedure.Facility);

            var possibleDepths = new HashSet<NCSurgicalAccessDepth>(procedure.StartingAccessDepths);
            foreach (var stepId in procedure.Steps)
            {
                Assert.That(_prototypes.TryIndex(stepId, out var step), Is.True, $"{procedure.ID}: {stepId}");
                Assert.That(step!.Duration, Is.GreaterThan(TimeSpan.Zero), step.ID);
                Assert.That(possibleDepths.Any(depth => AccessMatches(depth, step.RequiredAccessDepth)), Is.True,
                    $"{procedure.ID}: {step.ID} requires {step.RequiredAccessDepth} after {string.Join(", ", possibleDepths)}.");

                foreach (var tool in step.Tools)
                {
                    Assert.That(_prototypes.TryIndex(tool.Capability, out var capability), Is.True,
                        $"{step.ID}: {tool.Capability}");
                    Assert.That(_prototypes.HasIndex(capability!.RepresentativeTool), Is.True,
                        $"{step.ID}: {tool.Capability} has no concrete UI tool example.");
                }
                ValidateFacilityCapabilities(step.ID, step.Facility);

                var transitions = step.SuccessEffects
                    .Where(effect => effect.Kind == NCSurgeryEffectKind.SetAccessDepth)
                    .Select(effect => effect.AccessDepth)
                    .ToList();
                Assert.That(transitions, Has.Count.LessThanOrEqualTo(1), step.ID);
                if (transitions.Count == 1)
                {
                    possibleDepths.Clear();
                    possibleDepths.Add(transitions[0]);
                }
                else
                {
                    possibleDepths.RemoveWhere(depth => !AccessMatches(depth, step.RequiredAccessDepth));
                }
            }
        }

        // The authoritative override is guarded structurally: surgery data cannot regain RED/check fields.
        var forbidden = new[] { "skill", "check", "luck", "difficulty", "dv" };
        foreach (var type in new[]
                 {
                     typeof(NCSurgeryProcedurePrototype),
                     typeof(NCSurgeryStepPrototype),
                     typeof(NCSurgeryPractitionerComponent),
                 })
        {
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(forbidden.Any(word => member.Name.Contains(word, StringComparison.OrdinalIgnoreCase)),
                    Is.False,
                    $"{type.Name}.{member.Name} reintroduces a forbidden RED/check contract.");
            }
        }

        var requestFields = typeof(NCSurgeryConsoleSelectMessage).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var zoneRequestFields = typeof(NCSurgeryConsoleSelectZoneMessage).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var executeRequestFields = typeof(NCSurgeryConsoleExecuteStepMessage).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Multiple(() =>
        {
            Assert.That(requestFields.Select(field => field.Name),
                Is.EquivalentTo(new[] { "Procedure", "TargetCategory" }));
            Assert.That(requestFields.Any(field => field.FieldType == typeof(EntityUid) ||
                                                   field.FieldType == typeof(NetEntity) ||
                                                   field.FieldType == typeof(Guid)),
                Is.False,
                "An untrusted BUI request must not carry a patient, live target, graft, or wound identity.");
            Assert.That(zoneRequestFields.Select(field => field.Name), Is.EquivalentTo(new[] { "Zone" }));
            Assert.That(executeRequestFields, Is.Empty,
                "The step button advances only the server-owned current step; the client cannot choose an index.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LiveSurgeryRequiresAuthoritativePharmacology()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var wound = AddWound(patient, region, NCWoundType.Slash, 2f);
        PreparePatient(patient, table, table, airwayMonitored: true, administerAnesthetic: false);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");

        var request = new NCSurgeryStartRequest(
            NCActionResolutionId.New(),
            patient,
            surgeon,
            region,
            DeepWoundTreatment,
            table,
            table,
            wound);
        Assert.That(_surgery.StartSurgery(request, out _, out var failure), Is.False);
        Assert.That(failure, Is.EqualTo(NCSurgeryStartFailure.PatientConditionNotMet));

        Assert.That(_pharmacology.ApplyDose(patient, "NCDrugSurgicalAnesthetic", 5f).Accepted, Is.True);
        Assert.That(_surgery.StartSurgery(request with { ResolutionId = NCActionResolutionId.New() },
                out var session,
                out failure),
            Is.True,
            failure.ToString());
        Assert.That(SComp<NCSurgerySessionComponent>(session).Patient, Is.EqualTo(patient));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void VascularRepairIsIdempotentAndAppliesOnlyObservedEffects()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var scalpel = SSpawn("Scalpel");
        var hemostat = SSpawn("Hemostat");
        var retractor = SSpawn("Retractor");
        var cautery = SSpawn("Cautery");
        var suture = SSpawn("MedicatedSuture");

        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCSurgeryConsoleComponent>(table), Is.True);
            Assert.That(SEntMan.HasComponent<NCSurgeryGraftSlotComponent>(table), Is.True);
        });

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var woundId = AddWound(patient, region, NCWoundType.Ballistic, 7f);
        PreparePatient(patient, table, table, airwayMonitored: true);
        Authorize(surgeon, "NCSurgeryAuthorizationVascular");

        var sessionResolution = NCActionResolutionId.New();
        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    sessionResolution,
                    patient,
                    surgeon,
                    region,
                    VascularRepair,
                    table,
                    table,
                    woundId),
                out var sessionUid,
                out var startFailure),
            Is.True,
            startFailure.ToString());
        Assert.That(sessionUid, Is.EqualTo(region));
        var revisionBeforeSteps = SComp<NCMedicalBodyComponent>(patient).MedicalRevision;

        var wrongTool = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, scalpel));
        Assert.Multiple(() =>
        {
            Assert.That(wrongTool.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Rejected));
            Assert.That(wrongTool.Failure, Is.EqualTo(NCSurgeryStepFailure.ToolCapabilityMissing));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).CurrentStepIndex, Is.Zero);
        });

        var prepareAction = NCActionResolutionId.New();
        var prepare = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(prepareAction, surgeon, cautery));
        var duplicate = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(prepareAction, surgeon, cautery));
        Assert.Multiple(() =>
        {
            Assert.That(prepare.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Resolved));
            Assert.That(duplicate.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Duplicate));
            Assert.That(duplicate.Outcome, Is.EqualTo(prepare.Outcome));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).CurrentStepIndex, Is.EqualTo(1));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).History, Has.Count.EqualTo(1));
        });

        ResolveExpected(sessionUid, surgeon, scalpel, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(sessionUid, surgeon, hemostat, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(sessionUid, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        var vessel = ResolveExpected(sessionUid, surgeon, suture, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(sessionUid, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        var closure = ResolveExpected(sessionUid, surgeon, suture, NCSurgeryStepResolutionStatus.Completed);

        var session = SComp<NCSurgerySessionComponent>(sessionUid);
        var site = SComp<NCSurgicalSiteComponent>(region);
        var wound = SComp<NCWoundContainerComponent>(patient).Wounds[woundId];
        var bleeding = SComp<NCBleedingComponent>(patient);
        Assert.Multiple(() =>
        {
            Assert.That(vessel.Outcome, Is.EqualTo(NCSurgeryStepOutcome.Compromised));
            Assert.That(vessel.Conditions?.PatientAnalgesia, Is.GreaterThan(0f));
            Assert.That(vessel.Conditions?.PatientSedation, Is.GreaterThan(0f));
            Assert.That(vessel.Conditions?.PatientAnesthesia, Is.GreaterThan(0f));
            Assert.That(closure.Outcome, Is.EqualTo(NCSurgeryStepOutcome.Success));
            Assert.That(session.State, Is.EqualTo(NCSurgerySessionState.Completed));
            Assert.That(session.History, Has.Count.EqualTo(7));
            Assert.That(site.AccessDepth, Is.EqualTo(NCSurgicalAccessDepth.ClosedPostOp));
            Assert.That(site.ActiveProcedure, Is.Null);
            Assert.That(site.Contamination, Is.GreaterThan(0f));
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Clamped), Is.True);
            Assert.That(wound.ClosureState, Is.EqualTo(NCWoundClosureState.Sutured));
            Assert.That(bleeding.Sources.Single().CurrentRate, Is.Zero);
            Assert.That(bleeding.ActiveExternalRate, Is.Zero);
            // MedicatedSuture is a vanilla stack. Surgery consumes that physical resource instead of the
            // fallback counter reserved for custom non-stack tools.
            Assert.That(SComp<StackComponent>(suture).Count, Is.EqualTo(8));
            Assert.That(SComp<NCSurgeryPractitionerComponent>(surgeon).ActiveSession, Is.Null);
            Assert.That(SComp<NCMedicalBodyComponent>(patient).MedicalRevision,
                Is.GreaterThanOrEqualTo(revisionBeforeSteps + 7));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ChangedConditionsPauseAndAbortPreservesPhysicalSiteState()
    {
        var firstPatient = SSpawn(HumanMob);
        var secondPatient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var cautery = SSpawn("Cautery");
        var scalpel = SSpawn("Scalpel");

        Assert.That(_anatomy.TryGetOrgan(firstPatient, "Thorax", out var firstRegion), Is.True);
        Assert.That(_anatomy.TryGetOrgan(secondPatient, "Thorax", out var secondRegion), Is.True);
        var firstWound = AddWound(firstPatient, firstRegion, NCWoundType.Slash, 4f);
        var secondWound = AddWound(secondPatient, secondRegion, NCWoundType.Slash, 4f);
        PreparePatient(firstPatient, table, table, airwayMonitored: true);
        PreparePatient(secondPatient, table, table, airwayMonitored: true);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    firstPatient,
                    surgeon,
                    firstRegion,
                    DeepWoundTreatment,
                    table,
                    table,
                    firstWound),
                out var sessionUid,
                out var failure),
            Is.True,
            failure.ToString());

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    secondPatient,
                    surgeon,
                    secondRegion,
                    DeepWoundTreatment,
                    table,
                    table,
                    secondWound),
                out _,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCSurgeryStartFailure.SurgeonBusy));

        var patientState = SComp<NCSurgeryPatientStateComponent>(firstPatient);
        patientState.CurrentFacility = null;
        var paused = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, cautery));
        Assert.Multiple(() =>
        {
            Assert.That(paused.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Paused));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).State, Is.EqualTo(NCSurgerySessionState.Paused));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).PauseReason,
                Is.EqualTo(NCSurgeryInterruptionReason.PatientMoved));
            Assert.That(SComp<NCSurgicalSiteComponent>(firstRegion).ActiveProcedure, Is.Not.Null);
        });

        patientState.CurrentFacility = table;
        Assert.That(_surgery.TryResume(sessionUid, surgeon, out failure), Is.True, failure.ToString());
        ResolveExpected(sessionUid, surgeon, cautery, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(sessionUid, surgeon, scalpel, NCSurgeryStepResolutionStatus.Resolved);
        Assert.That(SComp<NCSurgicalSiteComponent>(firstRegion).AccessDepth,
            Is.EqualTo(NCSurgicalAccessDepth.Incised));

        var abortAction = NCActionResolutionId.New();
        Assert.That(
            _surgery.Abort(
                sessionUid,
                surgeon,
                abortAction,
                NCSurgeryAbortReason.Manual,
                out var abortFailure),
            Is.True,
            abortFailure.ToString());
        Assert.That(
            _surgery.Abort(
                sessionUid,
                surgeon,
                abortAction,
                NCSurgeryAbortReason.Manual,
                out abortFailure),
            Is.True,
            abortFailure.ToString());

        var site = SComp<NCSurgicalSiteComponent>(firstRegion);
        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).State, Is.EqualTo(NCSurgerySessionState.Aborted));
            Assert.That(site.AccessDepth, Is.EqualTo(NCSurgicalAccessDepth.Incised));
            Assert.That(site.ActiveProcedure, Is.Null);
            Assert.That(site.Contamination, Is.GreaterThanOrEqualTo(0.08f));
            Assert.That(SComp<NCSurgeryPractitionerComponent>(surgeon).ActiveSession, Is.Null);
        });

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    firstPatient,
                    surgeon,
                    firstRegion,
                    DeepWoundTreatment,
                    table,
                    table,
                    firstWound),
                out _,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCSurgeryStartFailure.StartingAccessMismatch));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CyberwareInstallationAndRemovalArePhysicalSurgeryProcedures()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var module = SSpawn("NCImplantNeuralLinkMk1");
        var cautery = SSpawn("Cautery");
        var scalpel = SSpawn("Scalpel");
        var hemostat = SSpawn("Hemostat");
        var retractor = SSpawn("Retractor");
        var multitool = SSpawn("Multitool");
        var suture = SSpawn("MedicatedSuture");

        Assert.That(_anatomy.TryGetOrgan(patient, "Head", out var head), Is.True);
        PreparePatient(patient, table, table, airwayMonitored: true);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");
        Authorize(surgeon, "NCSurgeryAuthorizationOrgan");

        Assert.That(_surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    head,
                    "NCSurgeryCyberwareInstallation",
                    table,
                    table,
                    Graft: module),
                out var installation,
                out var failure),
            Is.True,
            failure.ToString());

        ResolveExpected(installation, surgeon, cautery, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, scalpel, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, hemostat, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, multitool, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(installation, surgeon, suture, NCSurgeryStepResolutionStatus.Completed);

        var installed = SComp<NCCyberwareComponent>(module);
        Assert.Multiple(() =>
        {
            Assert.That(installed.InstalledSlot?.Id, Is.EqualTo("NCSlotNeuralCore"));
            Assert.That(SComp<ChildOrganComponent>(module).Parent, Is.EqualTo(head));
            Assert.That(installed.Calibrated, Is.True);
            Assert.That(installed.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Linked));
        });

        Assert.That(_surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    head,
                    "NCSurgeryCyberwareRemoval",
                    table,
                    table),
                out var removal,
                out failure),
            Is.True,
            failure.ToString());

        ResolveExpected(removal, surgeon, cautery, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, scalpel, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, hemostat, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, multitool, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, retractor, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(removal, surgeon, suture, NCSurgeryStepResolutionStatus.Completed);

        Assert.Multiple(() =>
        {
            Assert.That(installed.InstalledSlot, Is.Null);
            Assert.That(SComp<ChildOrganComponent>(module).Parent, Is.Null);
            Assert.That(installed.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Disconnected));
        });
    }

    private NCSurgeryStepResolution ResolveExpected(
        EntityUid sessionUid,
        EntityUid surgeon,
        EntityUid tool,
        NCSurgeryStepResolutionStatus expected)
    {
        var result = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, tool));
        Assert.That(result.Status, Is.EqualTo(expected), result.Failure.ToString());
        Assert.That(result.Failure, Is.EqualTo(NCSurgeryStepFailure.None));
        return result;
    }

    private void PreparePatient(
        EntityUid patient,
        EntityUid facility,
        EntityUid environment,
        bool airwayMonitored,
        bool administerAnesthetic = true)
    {
        var prepared = SEntMan.EnsureComponent<NCSurgeryPatientStateComponent>(patient);
        prepared.CurrentFacility = facility;
        prepared.CurrentEnvironment = environment;
        prepared.Movement = 0f;
        prepared.Restrained = true;
        prepared.AirwayMonitored = airwayMonitored;

        var physiology = SEntMan.EnsureComponent<NCPhysiologyComponent>(patient);
        physiology.Oxygenation = 1f;
        physiology.ShockStage = NCShockStage.None;
        physiology.Consciousness = NCConsciousnessState.Alert;

        if (administerAnesthetic)
        {
            Assert.That(
                _pharmacology.ApplyDose(patient, "NCDrugSurgicalAnesthetic", 5f).Accepted,
                Is.True);
        }
    }

    private void Authorize(EntityUid surgeon, ProtoId<NCSurgeryAuthorizationPrototype> authorization)
    {
        SEntMan.EnsureComponent<NCSurgeryPractitionerComponent>(surgeon).Authorizations.Add(authorization);
    }

    private Guid AddWound(EntityUid patient, EntityUid region, NCWoundType type, float rate)
    {
        var category = SComp<OrganComponent>(region).Category!.Value;
        var id = Guid.NewGuid();
        var wounds = SEntMan.EnsureComponent<NCWoundContainerComponent>(patient);
        wounds.Wounds[id] = new NCWoundRecord
        {
            WoundId = id,
            Prototype = "NCWoundBallistic",
            TargetZone = "NCZoneChest",
            RegionCategory = category,
            WoundType = type,
            TissueDepth = NCTissueDepth.Vascular,
            Severity = NCWoundSeverity.Severe,
            Stage = NCWoundStage.Open,
            ExternalBleedRate = rate,
        };

        var bleeding = SEntMan.EnsureComponent<NCBleedingComponent>(patient);
        bleeding.Sources.Add(new NCBleedingSourceRecord
        {
            SourceId = Guid.NewGuid(),
            WoundId = id,
            TargetZone = "NCZoneChest",
            RegionCategory = category,
            Channel = NCBleedingChannel.ExternalVenous,
            BaseRate = rate,
            CurrentRate = rate,
        });
        bleeding.ActiveExternalRate += rate;
        return id;
    }

    private void ValidateFacilityCapabilities(string owner, NCSurgeryFacilityRequirement requirement)
    {
        foreach (var capability in requirement.Capabilities)
            Assert.That(_prototypes.HasIndex(capability), Is.True, $"{owner}: {capability}");
    }

    private static bool AccessMatches(NCSurgicalAccessDepth actual, NCSurgicalAccessDepth required)
    {
        return actual == required ||
               actual == NCSurgicalAccessDepth.ClosedPostOp && required == NCSurgicalAccessDepth.Closed;
    }
}
