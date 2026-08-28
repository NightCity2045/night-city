// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Surgery;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Projectiles;
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
    private static readonly EntProtoId DebugMultitool = "NCSurgeryDebugMultitool";
    private static readonly ProtoId<NCFreeformSurgeryProfilePrototype> HumanFreeformSurgery = "NCHumanFreeformSurgery";
    private static readonly ProtoId<NCSurgeryToolCapabilityPrototype> DisinfectionCapability = "NCSurgeryToolDisinfection";
    private static readonly ProtoId<NCSurgeryToolCapabilityPrototype> IncisionCapability = "NCSurgeryToolIncision";
    private static readonly ProtoId<NCSurgeryToolCapabilityPrototype> AmputationCapability = "NCSurgeryToolAmputation";
    private static readonly ProtoId<NCSurgeryToolCapabilityPrototype> TransplantCapability = "NCSurgeryToolTransplant";
    private static readonly ProtoId<NCSurgeryToolCapabilityPrototype> CyberwareCapability = "NCSurgeryToolCyberware";
    private static readonly ProtoId<NCTargetZonePrototype> ChestZone = "NCZoneChest";
    private static readonly ProtoId<NCTargetZonePrototype> LeftArmZone = "NCZoneArmLeft";
    private static readonly ProtoId<NCTargetZonePrototype> HeadZone = "NCZoneHead";
    private static readonly ProtoId<OrganCategoryPrototype> HeartCategory = "Heart";
    private static readonly ProtoId<OrganCategoryPrototype> LeftArmCategory = "ArmLeft";
    private static readonly ProtoId<OrganCategoryPrototype> LeftArmBoneCategory = "ArmBoneLeft";
    private static readonly ProtoId<NCSurgeryProcedurePrototype> VascularRepair = "NCSurgeryVascularRepair";
    private static readonly ProtoId<NCSurgeryProcedurePrototype> DeepWoundTreatment = "NCSurgeryDeepWoundTreatment";
    private static readonly ProtoId<NCSurgeryProcedurePrototype> ForeignBodyExtraction = "NCSurgeryForeignBodyExtraction";
    private static readonly EntProtoId ExtractedBullet = "NCExtractedBullet";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCSurgerySystem _surgery = default!;
    [SidedDependency(Side.Server)] private NCSurgeryInteractionSystem _surgeryInteraction = default!;
    [SidedDependency(Side.Server)] private NCFreeformSurgeryEffectSystem _freeformEffects = default!;
    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;

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

                // Live procedures declare pharmacological preparation thresholds. They are deterministic
                // quality thresholds for compromised outcomes, not a hard permission gate.
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

        var field = _prototypes.Index(HumanFreeformSurgery);
        var configuredActions = field.Tools.Values.Select(definition => definition.Action).ToHashSet();
        Assert.Multiple(() =>
        {
            Assert.That(field.MaximumStrokePoints, Is.InRange(2, 64));
            Assert.That(field.MaximumPersistentIncisions, Is.InRange(1, 128));
            Assert.That(field.Tools.Keys, Is.SupersetOf(
                _prototypes.EnumeratePrototypes<NCSurgeryToolCapabilityPrototype>()
                    .Select(capability => (ProtoId<NCSurgeryToolCapabilityPrototype>) capability.ID)));
            Assert.That(configuredActions, Is.SupersetOf(Enum.GetValues<NCFreeformSurgeryAction>()));
            Assert.That(field.Structures.Keys.Select(category => category.Id),
                Is.SupersetOf(new[] { "Brain", "Heart", "Lungs", "Skull", "PelvisBone" }));
            Assert.That(field.CyberwareSlots.Keys,
                Is.SupersetOf(_prototypes.EnumeratePrototypes<NCCyberwareSlotPrototype>()
                    .Select(slot => (ProtoId<NCCyberwareSlotPrototype>) slot.ID)),
                "Every installable internal slot needs deterministic surgical-atlas geometry.");
        });

        foreach (var definition in field.Tools.Values)
        {
            Assert.Multiple(() =>
            {
                Assert.That(definition.MinimumDepth, Is.InRange(0f, 1f));
                Assert.That(definition.MaximumDepth, Is.InRange(definition.MinimumDepth, 1f));
                Assert.That(definition.Radius, Is.GreaterThan(0f));
                Assert.That(definition.PowerPerUnit, Is.GreaterThan(0f));
            });
        }

        foreach (var definition in field.CyberwareSlots.Values)
        {
            Assert.Multiple(() =>
            {
                Assert.That(definition.Position.X, Is.InRange(0f, 1f));
                Assert.That(definition.Position.Y, Is.InRange(0f, 1f));
                Assert.That(definition.Size.X, Is.GreaterThan(0f));
                Assert.That(definition.Size.Y, Is.GreaterThan(0f));
                Assert.That(definition.MinimumDepth, Is.InRange(0f, 1f));
            });
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

        var requestProperties = typeof(NCSurgeryDirectChoiceRequest).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.Multiple(() =>
        {
            Assert.That(requestProperties.Select(property => property.Name),
                Is.EquivalentTo(new[] { "OfferId", "Procedure", "TargetCategory" }));
            Assert.That(requestProperties.Any(property => property.PropertyType == typeof(EntityUid) ||
                                                        property.PropertyType == typeof(NetEntity) ||
                                                        property.PropertyType == typeof(Guid)),
                Is.False,
                "An untrusted direct choice must not carry a patient, tool, live target, graft, or wound identity.");
        });

        var freeformMessages = new[]
        {
            typeof(NCFreeformSurgeryState),
            typeof(NCFreeformSurgeryRefreshRequest),
            typeof(NCFreeformSurgerySelectionRequest),
            typeof(NCFreeformSurgeryCapabilityRequest),
            typeof(NCFreeformSurgeryStrokeRequest),
            typeof(NCFreeformSurgeryAttachRequest),
            typeof(NCFreeformSurgeryDetachRequest),
            typeof(NCFreeformSurgeryCloseRequest),
        };
        foreach (var message in freeformMessages)
        {
            var properties = message.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Assert.That(properties.Any(property => property.PropertyType == typeof(EntityUid) ||
                                                   property.PropertyType == typeof(NetEntity)),
                Is.False,
                $"{message.Name} must expose only its private session id, bounded input and opaque handles.");
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FreeformFieldDisinfectsAndIncisesAuthoritativePatientState()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var tool = SSpawn(DebugMultitool);
        var profile = _prototypes.Index(HumanFreeformSurgery);
        var toolComponent = SComp<NCSurgeryToolComponent>(tool);

        Assert.That(_anatomy.TryGetZone(patient, ChestZone, out _, out var region), Is.True);
        Assert.That(_anatomy.TryGetOrgan(patient, HeartCategory, out var heart), Is.True);
        var site = SComp<NCSurgicalSiteComponent>(region);
        var heartIntegrity = SComp<NCOrganIntegrityComponent>(heart);
        var integrityBefore = heartIntegrity.CurrentIntegrity;
        site.Contamination = 0.8f;

        var preparation = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            region,
            tool,
            toolComponent,
            ChestZone,
            NCWoundSurface.Front,
            0f,
            new[] { new Vector2(0.25f, 0.5f), new Vector2(0.75f, 0.5f) },
            profile.Tools[DisinfectionCapability],
            toolComponent.Capabilities[DisinfectionCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            null,
            false,
            false,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        Assert.Multiple(() =>
        {
            Assert.That(preparation.Accepted, Is.True);
            Assert.That(site.Contamination, Is.LessThan(0.8f));
            Assert.That(site.Incisions, Is.Empty);
        });

        var woundCount = SEntMan.EnsureComponent<NCWoundContainerComponent>(patient).Wounds.Count;
        var incision = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            region,
            tool,
            toolComponent,
            ChestZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.40f, 0.57f), new Vector2(0.64f, 0.57f) },
            profile.Tools[IncisionCapability],
            toolComponent.Capabilities[IncisionCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            heart,
            false,
            false,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        var wounds = SComp<NCWoundContainerComponent>(patient).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(incision.Accepted, Is.True);
            Assert.That(site.Incisions, Has.Count.EqualTo(1));
            Assert.That(site.Incisions[0].TargetZone, Is.EqualTo(ChestZone));
            Assert.That(site.Incisions[0].Depth, Is.EqualTo(0.8f));
            Assert.That(wounds, Has.Length.EqualTo(woundCount + 1));
            Assert.That(wounds.Any(wound => wound.InternalBleedRate > 0f), Is.True);
            Assert.That(heartIntegrity.CurrentIntegrity, Is.LessThan(integrityBefore));
            Assert.That(site.SpatialRevision, Is.EqualTo(2));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FreeformAmputationDetachesTheSelectedLimbInsteadOfItsBone()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var tool = SSpawn(DebugMultitool);
        var profile = _prototypes.Index(HumanFreeformSurgery);
        var toolComponent = SComp<NCSurgeryToolComponent>(tool);

        Assert.That(_anatomy.TryGetZone(patient, LeftArmZone, out _, out var arm), Is.True);
        Assert.That(_anatomy.TryGetOrgan(patient, LeftArmBoneCategory, out var armBone), Is.True);

        var result = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            arm,
            tool,
            toolComponent,
            LeftArmZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.2f, 0.5f), new Vector2(0.8f, 0.5f) },
            profile.Tools[AmputationCapability],
            toolComponent.Capabilities[AmputationCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            armBone,
            true,
            false,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(_anatomy.TryGetOrgan(patient, LeftArmCategory, out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(patient, LeftArmBoneCategory, out _), Is.False,
                "The internal arm bone must leave with the detached limb subtree.");
            Assert.That(SComp<OrganComponent>(arm).Body, Is.Null);
            Assert.That(SComp<ChildOrganComponent>(arm).Parent, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FreeformTransplantAttachesAStagedProsthesisToTheMissingZone()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var tool = SSpawn(DebugMultitool);
        var replacement = SSpawn("NCOrganHumanCyberArmLeft");
        var profile = _prototypes.Index(HumanFreeformSurgery);
        var toolComponent = SComp<NCSurgeryToolComponent>(tool);

        Assert.That(_detachment.TryDetachOrgan(patient, LeftArmCategory, out _), Is.True);
        Assert.That(_anatomy.TryGetOrgan(patient, "Torso", out var torso), Is.True);
        Assert.That(_targeting.TrySetSelection(
                surgeon,
                new NCTargetingState(NCTargetingMode.SelectedZone, LeftArmZone),
                out _,
                out _),
            Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(surgeon, patient, replacement), Is.True);

        var result = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            torso,
            tool,
            toolComponent,
            LeftArmZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) },
            profile.Tools[TransplantCapability],
            toolComponent.Capabilities[TransplantCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            null,
            false,
            false,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(_anatomy.TryGetOrgan(patient, LeftArmCategory, out var attached), Is.True);
            Assert.That(attached, Is.EqualTo(replacement));
            Assert.That(SComp<ChildOrganComponent>(replacement).Parent, Is.EqualTo(torso));
            Assert.That(SEntMan.HasComponent<NCSurgeryGameplaySelectionComponent>(surgeon), Is.False);
        });

    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FreeformCyberwareInstallsAStagedInternalImplant()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var tool = SSpawn(DebugMultitool);
        var implant = SSpawn("NCImplantNeuralLinkMk1");
        var profile = _prototypes.Index(HumanFreeformSurgery);
        var toolComponent = SComp<NCSurgeryToolComponent>(tool);

        Assert.That(_anatomy.TryGetOrgan(patient, "Head", out var head), Is.True);
        Assert.That(_targeting.TrySetSelection(
                surgeon,
                new NCTargetingState(NCTargetingMode.SelectedZone, HeadZone),
                out _,
                out _),
            Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(surgeon, patient, implant), Is.True);

        var blocked = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            head,
            tool,
            toolComponent,
            HeadZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) },
            profile.Tools[CyberwareCapability],
            toolComponent.Capabilities[CyberwareCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            null,
            false,
            true,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Accepted, Is.False);
            Assert.That(blocked.Failure, Is.EqualTo(NCFreeformSurgeryFailure.SurgicalAccessClosed));
            Assert.That(SComp<NCCyberwareComponent>(implant).InstalledSlot, Is.Null);
        });

        var site = SComp<NCSurgicalSiteComponent>(head);
        site.Incisions.Add(new NCSurgicalIncisionRecord
        {
            IncisionId = Guid.NewGuid(),
            TargetZone = HeadZone,
            Surface = NCWoundSurface.Front,
            Polyline = [new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f)],
            Depth = 0.8f,
        });

        var result = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            head,
            tool,
            toolComponent,
            HeadZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) },
            profile.Tools[CyberwareCapability],
            toolComponent.Capabilities[CyberwareCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            null,
            false,
            true,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        var installed = SComp<NCCyberwareComponent>(implant);
        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(installed.InstalledSlot?.Id, Is.EqualTo("NCSlotNeuralCore"));
            Assert.That(SComp<ChildOrganComponent>(implant).Parent, Is.EqualTo(head));
            Assert.That(installed.Calibrated, Is.True);
            Assert.That(installed.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Linked));
            Assert.That(SEntMan.HasComponent<NCSurgeryGameplaySelectionComponent>(surgeon), Is.False);
        });

        var removal = _freeformEffects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            patient,
            head,
            tool,
            toolComponent,
            HeadZone,
            NCWoundSurface.Front,
            0.8f,
            new[] { new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f) },
            profile.Tools[CyberwareCapability],
            toolComponent.Capabilities[CyberwareCapability],
            toolComponent.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            implant,
            false,
            true,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        Assert.Multiple(() =>
        {
            Assert.That(removal.Accepted, Is.True);
            Assert.That(installed.InstalledSlot, Is.Null);
            Assert.That(SComp<ChildOrganComponent>(implant).Parent, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DebugMultitoolCoversEverySurgeryInstrumentStage()
    {
        var toolUid = SSpawn(DebugMultitool);
        var tool = SComp<NCSurgeryToolComponent>(toolUid);
        var authorizationProvider = SComp<NCSurgeryAuthorizationProviderComponent>(toolUid);
        var procedures = _prototypes.EnumeratePrototypes<NCSurgeryProcedurePrototype>().ToList();
        var steps = _prototypes.EnumeratePrototypes<NCSurgeryStepPrototype>().ToList();
        var requiredCapabilities = steps
            .SelectMany(step => step.Tools)
            .Select(requirement => requirement.Capability)
            .ToHashSet();
        var requiredAuthorizations = procedures
            .SelectMany(procedure => procedure.RequiredAuthorizations)
            .ToHashSet();
        var procedureIds = procedures
            .Select(procedure => (ProtoId<NCSurgeryProcedurePrototype>) procedure.ID)
            .ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(tool.Broken, Is.False);
            Assert.That(tool.Sterility, Is.EqualTo(1f));
            Assert.That(tool.UsesRemaining, Is.LessThan(0),
                "The debug tool must not run out during a full procedure.");
            Assert.That(tool.SolutionResource, Is.Null,
                "The debug tool must not depend on a reagent container.");
            Assert.That(tool.Capabilities.Keys, Is.SupersetOf(requiredCapabilities));
            Assert.That(tool.StartingProcedures, Is.EquivalentTo(procedureIds),
                "Every production surgery procedure must be directly selectable with the debug tool.");
            Assert.That(authorizationProvider.Authorizations, Is.SupersetOf(requiredAuthorizations),
                "The debug tool must expose every authorization used by the production catalogue.");
        });

        foreach (var step in steps)
        {
            Assert.That(_surgery.CanToolPerform(toolUid, tool, step), Is.True,
                $"{DebugMultitool} cannot perform {step.ID}.");

            foreach (var requirement in step.Tools)
            {
                Assert.That(_surgery.CanToolSatisfy(toolUid, tool, requirement), Is.True,
                    $"{DebugMultitool} does not satisfy {step.ID}: {requirement.Capability}.");
            }
        }

        // Exercise the real resolver with one procedure that crosses disinfection, incision, hemostasis,
        // retraction, debridement and suturing. Clinical prerequisites remain authoritative; only the
        // physical instrument changes between a production workflow and this debug workflow.
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var wound = AddWound(patient, region, NCWoundType.Slash, 2f);
        PreparePatient(patient, table, table, airwayMonitored: true);
        SEntMan.EnsureComponent<NCSurgeryPractitionerComponent>(surgeon)
            .Authorizations.UnionWith(authorizationProvider.Authorizations);

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    region,
                    DeepWoundTreatment,
                    table,
                    table,
                    wound),
                out var sessionUid,
                out var startFailure),
            Is.True,
            startFailure.ToString());

        var procedure = _prototypes.Index(DeepWoundTreatment);
        for (var index = 0; index < procedure.Steps.Count; index++)
        {
            var expected = index == procedure.Steps.Count - 1
                ? NCSurgeryStepResolutionStatus.Completed
                : NCSurgeryStepResolutionStatus.Resolved;
            ResolveExpected(sessionUid, surgeon, toolUid, expected);
        }

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).State,
                Is.EqualTo(NCSurgerySessionState.Completed));
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).History,
                Has.Count.EqualTo(procedure.Steps.Count));
            Assert.That(tool.UsesRemaining, Is.LessThan(0),
                "A completed procedure must not consume the debug tool's infinite resource.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LiveSurgeryWithoutAnesthesiaStartsWithCompromisedOutcome()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var wound = AddWound(patient, region, NCWoundType.Slash, 2f);
        PreparePatient(patient, table, table, airwayMonitored: true, administerAnesthetic: false);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");
        var cautery = SSpawn("Cautery");

        var request = new NCSurgeryStartRequest(
            NCActionResolutionId.New(),
            patient,
            surgeon,
            region,
            DeepWoundTreatment,
            table,
            table,
            wound);
        Assert.That(_surgery.StartSurgery(request, out var session, out var failure),
            Is.True,
            failure.ToString());
        var result = _surgery.ResolveStep(
            session,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, cautery));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Resolved));
            Assert.That(result.Outcome, Is.EqualTo(NCSurgeryStepOutcome.Compromised));
            Assert.That(SComp<NCSurgerySessionComponent>(session).Patient, Is.EqualTo(patient));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ForeignBodyExtractionMaterializesOneInertForensicBullet()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var tool = SSpawn(DebugMultitool);

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var woundId = AddWound(patient, region, NCWoundType.Ballistic, 5f);
        var wound = SComp<NCWoundContainerComponent>(patient).Wounds[woundId];
        var foreignBodyId = Guid.NewGuid();
        wound.EmbeddedObjectId = foreignBodyId;
        wound.EmbeddedObjectEntityPrototype = ExtractedBullet;
        wound.SourceProjectilePrototype = "BulletPistol";

        PreparePatient(patient, table, table, airwayMonitored: true);
        var authorizations = SComp<NCSurgeryAuthorizationProviderComponent>(tool).Authorizations;
        SEntMan.EnsureComponent<NCSurgeryPractitionerComponent>(surgeon)
            .Authorizations.UnionWith(authorizations);

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    region,
                    ForeignBodyExtraction,
                    table,
                    table,
                    woundId),
                out var session,
                out var failure),
            Is.True,
            failure.ToString());

        var procedure = _prototypes.Index(ForeignBodyExtraction);
        for (var index = 0; index < procedure.Steps.Count; index++)
        {
            var expected = index == procedure.Steps.Count - 1
                ? NCSurgeryStepResolutionStatus.Completed
                : NCSurgeryStepResolutionStatus.Resolved;
            ResolveExpected(session, surgeon, tool, expected);
        }

        var matches = new List<(EntityUid Uid, NCExtractedForeignBodyComponent Component)>();
        var query = SEntMan.EntityQueryEnumerator<NCExtractedForeignBodyComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (component.ForeignBodyId == foreignBodyId)
                matches.Add((uid, component));
        }

        Assert.Multiple(() =>
        {
            Assert.That(wound.EmbeddedObjectId, Is.Null);
            Assert.That(wound.EmbeddedObjectEntityPrototype, Is.Null);
            Assert.That(wound.SourceProjectilePrototype, Is.Null);
            Assert.That(matches, Has.Count.EqualTo(1));
        });

        var extracted = matches.Single();
        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(extracted.Uid).EntityPrototype?.ID,
                Is.EqualTo(ExtractedBullet.Id));
            Assert.That(extracted.Component.SourceProjectilePrototype?.Id, Is.EqualTo("BulletPistol"));
            Assert.That(SEntMan.HasComponent<ProjectileComponent>(extracted.Uid), Is.False,
                "An extracted bullet must be inert and cannot collide as a live projectile.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SurgeryOnGroundStartsAndDeterministicallyAppliesFieldComplications()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var cautery = SSpawn("Cautery");

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var wound = AddWound(patient, region, NCWoundType.Slash, 2f);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");

        var request = new NCSurgeryStartRequest(
            NCActionResolutionId.New(),
            patient,
            surgeon,
            region,
            DeepWoundTreatment,
            TargetWoundId: wound);
        Assert.That(_surgery.StartSurgery(request, out var session, out var failure),
            Is.True,
            failure.ToString());

        var result = _surgery.ResolveStep(
            session,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, cautery));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Resolved));
            Assert.That(result.Outcome, Is.EqualTo(NCSurgeryStepOutcome.Compromised));
            Assert.That(result.Conditions?.FacilityPresent, Is.False);
            Assert.That(result.Conditions?.EnvironmentPresent, Is.False);
            Assert.That(result.Conditions?.PatientMovement, Is.EqualTo(1f));
            Assert.That(SComp<NCSurgicalSiteComponent>(region).Contamination, Is.GreaterThan(0f));
            Assert.That(SComp<NCSurgerySessionComponent>(session).State,
                Is.EqualTo(NCSurgerySessionState.Running));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DirectSurgerySkipsOnlyPrototypeMarkedPreparationPrefix()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var scalpel = SSpawn("Scalpel");

        Assert.That(_anatomy.TryGetOrgan(patient, "Thorax", out var region), Is.True);
        var wound = AddWound(patient, region, NCWoundType.Slash, 2f);
        PreparePatient(patient, table, table, airwayMonitored: true);
        Authorize(surgeon, "NCSurgeryAuthorizationGeneral");

        Assert.That(
            _surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    region,
                    DeepWoundTreatment,
                    table,
                    table,
                    wound,
                    InitialStepIndex: 1),
                out var session,
                out var failure),
            Is.True,
            failure.ToString());
        Assert.That(SComp<NCSurgerySessionComponent>(session).CurrentStepIndex, Is.EqualTo(1));
        ResolveExpected(session, surgeon, scalpel, NCSurgeryStepResolutionStatus.Resolved);

        Assert.That(
            _surgery.Abort(
                session,
                surgeon,
                NCActionResolutionId.New(),
                NCSurgeryAbortReason.Manual,
                out var abortFailure),
            Is.True,
            abortFailure.ToString());

        var secondPatient = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(secondPatient, "Thorax", out var secondRegion), Is.True);
        var secondWound = AddWound(secondPatient, secondRegion, NCWoundType.Slash, 2f);
        PreparePatient(secondPatient, table, table, airwayMonitored: true);

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
                    secondWound,
                    InitialStepIndex: 2),
                out _,
                out failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(NCSurgeryStartFailure.InvalidInitialStep));
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
    public void LeavingTableContinuesAsCompromisedAndAbortPreservesPhysicalSiteState()
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
        patientState.CurrentEnvironment = null;
        patientState.Movement = 1f;
        patientState.Restrained = false;
        patientState.AirwayMonitored = false;
        var fieldStep = _surgery.ResolveStep(
            sessionUid,
            new NCSurgeryStepRequest(NCActionResolutionId.New(), surgeon, cautery));
        Assert.Multiple(() =>
        {
            Assert.That(fieldStep.Status, Is.EqualTo(NCSurgeryStepResolutionStatus.Resolved));
            Assert.That(fieldStep.Outcome, Is.EqualTo(NCSurgeryStepOutcome.Compromised));
            Assert.That(fieldStep.Conditions?.FacilityPresent, Is.False);
            Assert.That(fieldStep.Conditions?.EnvironmentPresent, Is.False);
            Assert.That(SComp<NCSurgerySessionComponent>(sessionUid).State,
                Is.EqualTo(NCSurgerySessionState.Running));
            Assert.That(SComp<NCSurgicalSiteComponent>(firstRegion).ActiveProcedure, Is.Not.Null);
        });

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
        Assert.That(_targeting.TrySetSelection(
                surgeon,
                new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneHead"),
                out _,
                out _),
            Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(surgeon, patient, module), Is.True,
            "Clicking an internal implant on a compatible selected region must stage installation.");
        var staged = SComp<NCSurgeryGameplaySelectionComponent>(surgeon);
        Assert.Multiple(() =>
        {
            Assert.That(staged.Procedure.Id, Is.EqualTo("NCSurgeryCyberwareInstallation"));
            Assert.That(staged.TargetRegion, Is.EqualTo(head));
            Assert.That(staged.Graft, Is.EqualTo(module));
        });

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

    [Test]
    [RunOnSide(Side.Server)]
    public void MechanicalReplacementIsACompatibleCalibratedLiveGraft()
    {
        var patient = SSpawn(HumanMob);
        var surgeon = SSpawn(HumanMob);
        var table = SSpawn(OperatingTable);
        var debugTool = SSpawn(DebugMultitool);
        var replacement = SSpawn("NCOrganHumanCyberArmLeft");
        var wrongSide = SSpawn("NCOrganHumanCyberArmRight");

        Assert.That(_detachment.TryDetachOrgan(patient, "ArmLeft", out _), Is.True);
        Assert.That(_anatomy.TryGetOrgan(patient, "Torso", out var torso), Is.True);
        Assert.That(_targeting.TrySetSelection(
                surgeon,
                new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneArmLeft"),
                out _,
                out _),
            Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(surgeon, patient, wrongSide), Is.False,
            "A right cyberarm must never bind to a selected missing left arm.");
        Assert.That(_surgeryInteraction.TryStageCyberware(surgeon, patient, replacement), Is.True,
            "Clicking the matching cyberarm on the patient must stage limb reattachment.");
        var staged = SComp<NCSurgeryGameplaySelectionComponent>(surgeon);
        Assert.Multiple(() =>
        {
            Assert.That(staged.Patient, Is.EqualTo(patient));
            Assert.That(staged.Procedure.Id, Is.EqualTo("NCSurgeryLimbReattachment"));
            Assert.That(staged.TargetRegion, Is.EqualTo(torso));
            Assert.That(staged.Graft, Is.EqualTo(replacement));
        });
        PreparePatient(patient, table, table, airwayMonitored: true);
        Authorize(surgeon, "NCSurgeryAuthorizationOrthopedic");
        Authorize(surgeon, "NCSurgeryAuthorizationVascular");
        Authorize(surgeon, "NCSurgeryAuthorizationTransplant");

        var payload = SComp<NCDetachedOrganMedicalDataComponent>(replacement);
        var integrity = SComp<NCOrganIntegrityComponent>(replacement);
        Assert.Multiple(() =>
        {
            Assert.That(payload.SourceBodyProfile.Id, Is.EqualTo("Human"));
            Assert.That(payload.RequiredParentCategory.Id, Is.EqualTo("Torso"));
            Assert.That(integrity.Material, Is.EqualTo(NCAnatomicalMaterialKind.Mechanical));
        });

        Assert.That(_surgery.StartSurgery(
                new NCSurgeryStartRequest(
                    NCActionResolutionId.New(),
                    patient,
                    surgeon,
                    torso,
                    "NCSurgeryLimbReattachment",
                    table,
                    table,
                    Graft: replacement),
                out var session,
                out var failure),
            Is.True,
            failure.ToString());

        for (var index = 0; index < 8; index++)
            ResolveExpected(session, surgeon, debugTool, NCSurgeryStepResolutionStatus.Resolved);
        ResolveExpected(session, surgeon, debugTool, NCSurgeryStepResolutionStatus.Completed);

        Assert.That(_anatomy.TryGetOrgan(patient, "ArmLeft", out var attached), Is.True);
        var cyberware = SComp<NCCyberwareComponent>(replacement);
        Assert.Multiple(() =>
        {
            Assert.That(attached, Is.EqualTo(replacement));
            Assert.That(SComp<OrganComponent>(replacement).Body, Is.EqualTo(patient));
            Assert.That(SComp<ChildOrganComponent>(replacement).Parent, Is.EqualTo(torso));
            Assert.That(SEntMan.HasComponent<NCDetachedOrganMedicalDataComponent>(replacement), Is.False);
            Assert.That(cyberware.Calibrated, Is.True);
            Assert.That(cyberware.NeuralState, Is.EqualTo(NCCyberwareNeuralState.Linked));
            Assert.That(cyberware.Operational, Is.True);
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
