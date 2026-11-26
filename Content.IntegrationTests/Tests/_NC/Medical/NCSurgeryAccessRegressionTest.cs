// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Surgery;
using Content.Server._NC.Medical.Wounds;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Regression checks for reported surgery bugs: a hemostat must keep a wound's bleeding down, and a spinal
/// implant that needs the neural link must be installable, fixed, connected and calibrated through the same
/// effect path the operating interface uses. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCSurgeryAccessRegressionTest : GameTest
{
    private static readonly ProtoId<NCFreeformSurgeryProfilePrototype> HumanField = "NCHumanFreeformSurgery";
    [SidedDependency(Side.Server)] private NCCyberwareSystem _cyberware = default!;
    [SidedDependency(Side.Server)] private NCSurgeryInteractionSystem _surgeryInteraction = default!;
    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;
    [SidedDependency(Side.Server)] private NCFreeformSurgeryEffectSystem _effects = default!;
    [SidedDependency(Side.Server)] private NCWoundProgressionSystem _progression = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _battery = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    [RunOnSide(Side.Server)]
    public void HemostasisKeepsTheBleedingDownAfterTheNextProgressionTick()
    {
        var (body, user, _) = Scene();
        var arm = Organ(body, "ArmLeft");
        Open(arm, "NCZoneArmLeft");
        var woundId = CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 3f);
        var wound = SComp<NCWoundContainerComponent>(body).Wounds[woundId];
        Assert.That(wound.ExternalBleedRate, Is.GreaterThan(0f), "the test wound must bleed");

        var tool = SpawnAt("NCSurgeryDebugMultitool", user);
        var point = new Vector2(wound.VisualX / 255f, wound.VisualY / 255f);
        var stroke = Stroke(body, user, arm, tool, null, "NCSurgeryToolHemostasis", "NCZoneArmLeft",
            [point, point + new Vector2(0.01f, 0f)], wound.Surface, woundId);
        var before = wound.ExternalBleedRate;
        var applied = _effects.ApplyStroke(stroke);
        Assert.That(applied.Accepted, Is.True, $"hemostat stroke refused: {applied.Failure}");
        var clamped = wound.ExternalBleedRate;
        Assert.That(clamped, Is.LessThan(before), "the hemostat must lower the bleeding at once");

        // Several scheduler intervals later the wound recomputes its own rates; a clamped vessel must stay clamped.
        for (var i = 1; i <= 4; i++)
        {
            var now = TimeSpan.FromSeconds(5 * i);
            SComp<NCCirculationComponent>(body).Perfusion = 1f;
            _progression.AdvanceBody(body, TimeSpan.FromSeconds(5), now);
            Assert.That(wound.HealingProgress, Is.GreaterThan(0f), "the healing branch must have run");
            Assert.That(wound.ExternalBleedRate, Is.LessThanOrEqualTo(clamped + 0.0001f),
                $"the bleeding came back after tick {i}");
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SandevistanCanBeMountedFixedConnectedAndCalibratedOnceTheNeuralLinkIsInstalled()
    {
        var (body, user, _) = Scene();
        var tool = SpawnAt("NCCyberwareServiceTool", user);

        // The neural link goes first, into the head, and must be calibrated for anything that depends on it.
        var head = Organ(body, "Head");
        Open(head, "NCZoneHead");
        var link = SpawnAt("NCImplantNeuralLinkMk1", user);
        Stage(user, body, link, "NCZoneHead");
        var linkMount = _effects.ApplyStroke(Stroke(body, user, head, tool, null,
            "NCSurgeryToolCyberware", "NCZoneHead"));
        Assert.That(linkMount.Accepted, Is.True, "the neural link must mount");
        Medical(body, user, head, tool, link, "NCZoneHead");
        var linkTest = _effects.ApplyStroke(Stroke(body, user, head, tool, link,
            "NCSurgeryToolCyberwareCalibration", "NCZoneHead"));
        Assert.That(linkTest.Accepted, Is.True, $"the neural link must calibrate ({linkTest.Failure})");
        Assert.That(SComp<NCCyberwareComponent>(link).Operational, Is.True);

        // The sandevistan goes into the chest next to the spine.
        var thorax = Organ(body, "Thorax");
        Open(thorax, "NCZoneChest");
        var burst = SpawnAt("NCSandevistanImplant", user);
        Stage(user, body, burst, "NCZoneChest");
        var mount = _effects.ApplyStroke(Stroke(body, user, thorax, tool, null,
            "NCSurgeryToolCyberware", "NCZoneChest"));
        Assert.That(mount.Accepted, Is.True, "the sandevistan must mount into the open chest");
        Assert.That(SComp<ChildOrganComponent>(burst).Parent, Is.Not.EqualTo(default(EntityUid)));

        Medical(body, user, thorax, tool, burst, "NCZoneChest");

        var calibration = _effects.ApplyStroke(Stroke(body, user, thorax, tool, burst,
            "NCSurgeryToolCyberwareCalibration", "NCZoneChest"));
        Assert.That(calibration.Accepted, Is.True,
            $"the sandevistan must connect to the neural link ({calibration.Failure})");
        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCCyberwareComponent>(burst).Calibrated, Is.True);
            Assert.That(SComp<NCCyberwareComponent>(burst).Operational, Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RecuttingAnOpenedZoneReusesTheExistingOpening()
    {
        var (body, user, _) = Scene();
        var head = Organ(body, "Head");
        Open(head, "NCZoneHead");
        var site = SComp<NCSurgicalSiteComponent>(head);
        var original = site.Incisions.Single();

        foreach (var surface in new[] { NCWoundSurface.Front, NCWoundSurface.Rear, NCWoundSurface.Left })
        {
            var cut = SpawnAt("NCSurgeryDebugMultitool", user);
            var result = _effects.ApplyStroke(Stroke(body, user, head, cut, null, "NCSurgeryToolIncision", "NCZoneHead",
                [new Vector2(0.5f, 0.3f), new Vector2(0.5f, 0.5f)], surface));
            Assert.That(result.Accepted, Is.True, $"recut from {surface} must be accepted ({result.Failure})");
            Assert.That(site.Incisions, Has.Count.EqualTo(1), $"recut from {surface} must not open a second site");
        }

        var survivor = site.Incisions.Single();
        Assert.That(survivor.IncisionId, Is.EqualTo(original.IncisionId));
        Assert.That(survivor.BoneCasingOpen, Is.True, "an opened skull must stay open after another cut");
        Assert.That(survivor.EdgeSeparation, Is.GreaterThanOrEqualTo(0.3f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ClosedAccessNamesTheMissingStepAndIgnoresOtherZones()
    {
        var (body, user, _) = Scene();
        var thorax = Organ(body, "Thorax");
        var abdomen = Organ(body, "Abdomen");
        Open(thorax, "NCZoneChest");

        // No abdomen incision at all: the cut is missing, however open the chest is.
        var clamp = SpawnAt("NCSurgeryDebugMultitool", user);
        var noCut = _effects.ApplyStroke(Stroke(body, user, abdomen, clamp, null, "NCSurgeryToolHemostasis",
            "NCZoneAbdomen"));
        Assert.That(noCut.Failure, Is.EqualTo(NCFreeformSurgeryFailure.SurgicalAccessTooShallow));

        // A deep but unretracted abdomen incision needs a retractor.
        var site = SComp<NCSurgicalSiteComponent>(abdomen);
        site.Incisions.Add(new NCSurgicalIncisionRecord
        {
            IncisionId = Guid.NewGuid(), TargetZone = "NCZoneAbdomen", Depth = 0.95f,
            EdgeSeparation = 0f, Surface = NCWoundSurface.Front,
            Polyline = [new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f)],
            SourceResolutionId = NCActionResolutionId.New(),
            CreatedAt = SGameTiming.CurTime, ModifiedAt = SGameTiming.CurTime,
        });
        var closed = _effects.ApplyStroke(Stroke(body, user, abdomen, clamp, null, "NCSurgeryToolHemostasis",
            "NCZoneAbdomen"));
        Assert.That(closed.Failure, Is.EqualTo(NCFreeformSurgeryFailure.SurgicalAccessNotRetracted));

        var retractor = _effects.ApplyStroke(Stroke(body, user, abdomen, clamp, null, "NCSurgeryToolRetraction",
            "NCZoneAbdomen"));
        Assert.That(retractor.Accepted, Is.True, $"retraction must open the abdomen ({retractor.Failure})");
        var open = _effects.ApplyStroke(Stroke(body, user, abdomen, clamp, null, "NCSurgeryToolHemostasis",
            "NCZoneAbdomen"));
        Assert.That(open.Failure, Is.Not.EqualTo(NCFreeformSurgeryFailure.SurgicalAccessNotRetracted));
        Assert.That(open.Failure, Is.Not.EqualTo(NCFreeformSurgeryFailure.SurgicalAccessTooShallow));
    }

    private void Medical(EntityUid body, EntityUid user, EntityUid region, EntityUid tool, EntityUid module,
        ProtoId<NCTargetZonePrototype> zone)
    {
        foreach (var capability in new[]
                 {
                     "NCSurgeryToolFixMedical", "NCSurgeryToolConnectMedical", "NCSurgeryToolCheckMedical",
                 })
        {
            var step = _effects.ApplyStroke(Stroke(body, user, region, tool, module, capability, zone));
            Assert.That(step.Accepted, Is.True, $"stage {capability} of {zone} must be accepted ({step.Failure})");
        }
    }

    private (EntityUid Body, EntityUid User, EntityUid Cell) Scene()
    {
        _maps.CreateMap(out var map);
        var body = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
        var user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0, 1), map));
        return (body, user, NCCyberwareTestPower.Install(SEntMan, _cyberware, _slots, _battery, body, 1000f));
    }

    private EntityUid Organ(EntityUid body, ProtoId<OrganCategoryPrototype> category) =>
        SComp<BodyComponent>(body).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category == category);

    private EntityUid SpawnAt(EntProtoId prototype, EntityUid target) =>
        SEntMan.SpawnEntity(prototype, SComp<TransformComponent>(target).Coordinates);

    private void Stage(EntityUid user, EntityUid body, EntityUid module, ProtoId<NCTargetZonePrototype> zone)
    {
        Assert.That(_targeting.TrySetSelection(user, new NCTargetingState(NCTargetingMode.SelectedZone, zone),
            out _, out _), Is.True);
        Assert.That(_surgeryInteraction.TryStageCyberware(user, body, module), Is.True);
    }

    private void Open(EntityUid region, ProtoId<NCTargetZonePrototype> zone)
    {
        SComp<NCSurgicalSiteComponent>(region).Incisions.Add(new NCSurgicalIncisionRecord
        {
            IncisionId = Guid.NewGuid(), TargetZone = zone, Depth = 0.9f,
            EdgeSeparation = 0.3f, BoneCasingOpen = true, Surface = NCWoundSurface.Front,
            Polyline = [new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f)],
            SourceResolutionId = NCActionResolutionId.New(),
            CreatedAt = SGameTiming.CurTime, ModifiedAt = SGameTiming.CurTime,
        });
    }

    private NCFreeformSurgeryEffectContext Stroke(EntityUid body, EntityUid user, EntityUid region, EntityUid tool,
        EntityUid? target, ProtoId<NCSurgeryToolCapabilityPrototype> capability, ProtoId<NCTargetZonePrototype> zone,
        Vector2[]? points = null, NCWoundSurface surface = NCWoundSurface.Front, Guid? woundId = null)
    {
        var profile = _prototypes.Index(HumanField);
        return new NCFreeformSurgeryEffectContext(user, body, region, tool, SComp<NCSurgeryToolComponent>(tool), zone,
            surface, 0.9f, points ?? [new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f)],
            profile.Tools[capability], 1f, 1f, profile.UnpreparedFieldContamination, profile.MinimumOpenRetraction,
            target, false, zone == "NCZoneHead", profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold, profile.IncidentalBoneBreakThreshold,
            WoundId: woundId, ResolutionId: NCActionResolutionId.New());
    }

    private Guid CreateWound(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        ProtoId<NCTraumaProfilePrototype> traumaProfile,
        float postArmorDamage)
    {
        var before = SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var existing)
            ? existing.Wounds.Keys.ToHashSet()
            : [];
        var request = new NCCombatTraumaRequest(
            NCActionResolutionId.New(), 1, 1, body, null, null, null, zone, region, traumaProfile,
            new DamageSpecifier(), FixedPoint2.New(postArmorDamage), FixedPoint2.Zero,
            FixedPoint2.New(postArmorDamage), FixedPoint2.Zero, Vector2.Zero, Vector2.UnitX,
            NCTraumaFlags.ArmorPenetrated, 0f, TimeSpan.Zero);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);
        Assert.That(trauma.Applied, Is.True);
        return SComp<NCWoundContainerComponent>(body).Wounds.Keys.Single(id => !before.Contains(id));
    }
}
