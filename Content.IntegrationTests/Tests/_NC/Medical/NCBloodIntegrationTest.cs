// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Decals;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Physiology;
using Content.Server.Body.Systems;
using Content.Server.Fluids.EntitySystems;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Decals;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCCirculationBloodSystem))]
public sealed class NCBloodIntegrationTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCCirculationBloodSystem _blood = default!;
    [SidedDependency(Side.Server)] private NCBleedingSystem _bleeding = default!;
    [SidedDependency(Side.Server)] private NCEmergencyCareSystem _emergency = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySystem _physiology = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private BloodstreamSystem _upstream = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task ExternalBleedingCreatesPuddleAndInternalBleedingDoesNot()
    {
        var map = await Pair.CreateTestMap();
        EntityUid external = default;
        EntityUid internalBody = default;
        float puddleAfterExternal = 0f;

        await Server.WaitPost(() =>
        {
            external = Server.EntMan.SpawnAtPosition(HumanMob, map.GridCoords);
            internalBody = Server.EntMan.SpawnAtPosition(HumanMob, map.GridCoords);
            ConfigureSource(Server.EntMan, external, NCBleedingChannel.ExternalVenous, 20f);
            ConfigureSource(Server.EntMan, internalBody, NCBleedingChannel.Internal, 20f);

            var externalBefore = Server.EntMan.GetComponent<NCCirculationComponent>(external).BloodVolume;
            Assert.That(Server.System<NCBleedingSystem>().AdvanceBody(external, TimeSpan.FromSeconds(1)),
                Is.EqualTo(20f).Within(0.01f));
            Assert.That(Server.EntMan.GetComponent<NCCirculationComponent>(external).BloodVolume,
                Is.EqualTo(externalBefore - 20f).Within(0.01f));

            var puddle = FindPuddle(Server.EntMan, Server.System<SharedMapSystem>(), map.Grid);
            Assert.That(puddle, Is.Not.Null, "External loss must use upstream TryBleedOut and spill blood.");
            puddleAfterExternal = (float) Server.System<PuddleSystem>()
                .CurrentVolume(puddle!.Value.Owner, puddle.Value.Comp);

            var internalBefore = Server.EntMan.GetComponent<NCCirculationComponent>(internalBody).BloodVolume;
            Assert.That(Server.System<NCBleedingSystem>().AdvanceBody(internalBody, TimeSpan.FromSeconds(1)),
                Is.EqualTo(20f).Within(0.01f));
            Assert.That(Server.EntMan.GetComponent<NCCirculationComponent>(internalBody).BloodVolume,
                Is.EqualTo(internalBefore - 20f).Within(0.01f));

            var samePuddle = FindPuddle(Server.EntMan, Server.System<SharedMapSystem>(), map.Grid);
            Assert.That(samePuddle, Is.Not.Null);
            Assert.That((float) Server.System<PuddleSystem>()
                    .CurrentVolume(samePuddle!.Value.Owner, samePuddle.Value.Comp),
                Is.EqualTo(puddleAfterExternal).Within(0.01f),
                "Internal bleeding must not add solution to the floor puddle.");

            Assert.That(Server.System<NCAnatomySystem>().TryGetOrgan(internalBody, "Thorax", out var thorax), Is.True);
            Assert.That(Server.EntMan.GetComponent<NCCavityFluidComponent>(thorax).BloodVolume,
                Is.EqualTo(20f).Within(0.01f));
            Assert.That(Server.EntMan.GetComponent<BloodstreamComponent>(external).BleedAmount, Is.Zero,
                "Legacy BleedAmount must never cause a second loss pass.");
        });

        await Server.WaitPost(() => Server.EntMan.DeleteEntity(map.MapUid));
    }

    [Test]
    public async Task ImpactBloodCreatesOneBoundedCleanableDecalExactlyOnce()
    {
        var map = await Pair.CreateTestMap();

        await Server.WaitPost(() =>
        {
            var body = Server.EntMan.SpawnAtPosition(HumanMob, map.GridCoords);
            var request = CreateBallisticRequest(body);
            var first = new NCCombatTraumaRequestedEvent(request);
            Server.EntMan.EventBus.RaiseLocalEvent(body, ref first);

            var decals = Server.System<DecalSystem>().GetDecalsInRange(
                map.Grid.Owner,
                map.GridCoords.Position,
                2f,
                decal => decal.Id == "splatter");
            Assert.Multiple(() =>
            {
                Assert.That(first.Applied, Is.True);
                Assert.That(decals, Has.Count.EqualTo(1));
                Assert.That(decals.Single().Decal.Cleanable, Is.True);
            });

            var duplicate = new NCCombatTraumaRequestedEvent(request);
            Server.EntMan.EventBus.RaiseLocalEvent(body, ref duplicate);
            decals = Server.System<DecalSystem>().GetDecalsInRange(
                map.Grid.Owner,
                map.GridCoords.Position,
                2f,
                decal => decal.Id == "splatter");
            Assert.Multiple(() =>
            {
                Assert.That(duplicate.Applied, Is.False);
                Assert.That(decals, Has.Count.EqualTo(1),
                    "Replaying one impact must not create another persistent decal.");
            });
        });

        await Server.WaitPost(() => Server.EntMan.DeleteEntity(map.MapUid));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CavityAndCirculationAreBoundedByConfiguredCapacity()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var before = circulation.BloodVolume;
        var internalByRegion = new Dictionary<ProtoId<Content.Shared.Body.OrganCategoryPrototype>, float>
        {
            ["Thorax"] = before * 2f,
        };

        Assert.That(_blood.TryApplyBleedingLoss(body, 0f, internalByRegion, 0f, out var application), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "Thorax", out var thorax), Is.True);
        var cavity = SComp<NCCavityFluidComponent>(thorax);

        Assert.Multiple(() =>
        {
            Assert.That(application.TotalLost, Is.EqualTo(before).Within(0.01f));
            Assert.That(circulation.BloodVolume, Is.Zero);
            Assert.That(cavity.BloodVolume, Is.EqualTo(cavity.MaximumBloodVolume).Within(0.01f));
            Assert.That(cavity.Pressure, Is.InRange(0f, 1f));
            Assert.That(cavity.OrganCompression, Is.InRange(0f, 1f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TransfusionCompatibilityMutatesAuthorityAndSolutionTogether()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var profile = _physiology.GetProfile(body);
        circulation.BloodVolume = profile.NominalBloodVolume - 600f;
        circulation.TransfusionCompatibility = "NC-A-Positive";
        Assert.That(_blood.SynchronizeSolution(body), Is.True);

        var compatibility = new Dictionary<string, List<string>>
        {
            ["NC-O-Negative"] = ["NC-A-Positive"],
            ["NC-B-Positive"] = ["NC-B-Positive"],
        };
        var rejected = _blood.TryTransfuse(
            body,
            "NC-B-Positive",
            450f,
            1f,
            compatibility);
        var afterRejected = circulation.BloodVolume;
        var accepted = _emergency.TransfuseBlood(body, SSpawn("Bloodpack"), TimeSpan.Zero);
        var upstreamFraction = _upstream.GetBloodLevel((body, SComp<BloodstreamComponent>(body)));

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(NCBloodTransfusionOutcome.Incompatible));
            Assert.That(afterRejected, Is.EqualTo(profile.NominalBloodVolume - 600f));
            Assert.That(accepted.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(upstreamFraction,
                Is.EqualTo(circulation.BloodVolume / profile.NominalBloodVolume).Within(0.001f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DetachedCavityRetainsFluidAndSameEntityReturnsOnReattach()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "Head", out var head), Is.True);
        var cavity = SComp<NCCavityFluidComponent>(head);
        cavity.BloodVolume = 42f;
        cavity.Pressure = cavity.BloodVolume / cavity.PressureFullScaleVolume;
        cavity.OrganCompression = cavity.BloodVolume / cavity.CompressionFullScaleVolume;

        Assert.That(_detachment.TryDetachOrgan(body, "Head", out var detached), Is.True);
        Assert.That(detached, Is.EqualTo(head));
        Assert.That(SComp<NCCavityFluidComponent>(detached).BloodVolume, Is.EqualTo(42f));
        Assert.That(_detachment.TryAttachOrgan(body, "Torso", detached), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "Head", out var restored), Is.True);
        Assert.That(restored, Is.EqualTo(head));
        Assert.That(SComp<NCCavityFluidComponent>(restored).BloodVolume, Is.EqualTo(42f));
    }

    private static void ConfigureSource(
        IEntityManager entities,
        EntityUid body,
        NCBleedingChannel channel,
        float rate)
    {
        var bleeding = entities.EnsureComponent<NCBleedingComponent>(body);
        bleeding.Sources.Clear();
        bleeding.OverflowExternalRate = 0f;
        bleeding.OverflowInternalRate = 0f;
        bleeding.OverflowInternalRatesByRegion.Clear();
        bleeding.Sources.Add(new NCBleedingSourceRecord
        {
            SourceId = Guid.NewGuid(),
            WoundId = Guid.NewGuid(),
            TargetZone = "NCZoneChest",
            RegionCategory = "Thorax",
            Channel = channel,
            BaseRate = rate,
            CurrentRate = rate,
        });
    }

    private static Entity<PuddleComponent>? FindPuddle(
        IEntityManager entities,
        SharedMapSystem maps,
        Entity<MapGridComponent> grid)
    {
        foreach (var uid in maps.GetAnchoredEntities(grid, grid.Comp, new Vector2i(0, 0)))
        {
            if (entities.TryGetComponent<PuddleComponent>(uid, out var puddle))
                return (uid, puddle);
        }

        return null;
    }

    private static NCCombatTraumaRequest CreateBallisticRequest(EntityUid body)
    {
        return new NCCombatTraumaRequest(
            NCActionResolutionId.New(),
            1,
            1,
            body,
            null,
            null,
            null,
            "NCZoneLegLeft",
            "LegLeft",
            "BallisticImpact",
            new DamageSpecifier(),
            FixedPoint2.New(10),
            FixedPoint2.Zero,
            FixedPoint2.New(10),
            FixedPoint2.Zero,
            Vector2.Zero,
            Vector2.UnitX,
            NCTraumaFlags.ArmorPenetrated,
            0.1f,
            TimeSpan.Zero);
    }
}
