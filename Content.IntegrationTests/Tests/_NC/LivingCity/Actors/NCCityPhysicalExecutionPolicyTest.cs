// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Groups;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Actors;

[TestFixture]
[TestOf(typeof(NCCityPhysicalExecutionSystem))]
public sealed class NCCityPhysicalExecutionPolicyTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public void DefaultPhysicalExecutionProfilesLoadFromYaml()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var policy = prototypes.Index<NCCityPhysicalExecutionPolicyPrototype>(
            new ProtoId<NCCityPhysicalExecutionPolicyPrototype>("NCDefaultCityPhysicalExecutionPolicy"));
        var navigation = prototypes.Index<NCCityPhysicalNavigationProfilePrototype>(
            new ProtoId<NCCityPhysicalNavigationProfilePrototype>("NCDefaultPedestrianPhysicalNavigation"));
        var stateTree = prototypes.Index<NCCityPhysicalStateTreePrototype>(
            new ProtoId<NCCityPhysicalStateTreePrototype>("NCDefaultPhysicalStateTree"));
        var perception = prototypes.Index<NCCityLocalPerceptionProfilePrototype>(
            new ProtoId<NCCityLocalPerceptionProfilePrototype>("NCDefaultLocalPerception"));
        var perceptionPolicy = prototypes.Index<NCCityLocalPerceptionPolicyPrototype>(
            new ProtoId<NCCityLocalPerceptionPolicyPrototype>("NCDefaultLocalPerceptionPolicy"));

        Assert.Multiple(() =>
        {
            Assert.That(policy.MaximumQueuedIntentLoads, Is.EqualTo(256));
            Assert.That(policy.MaximumExecutionUpdatesPerTick, Is.EqualTo(24));
            Assert.That(policy.MaximumWaypoints, Is.EqualTo(256));
            Assert.That(policy.MaximumImmediateWaypointCompletions, Is.EqualTo(8));
            Assert.That(policy.MaximumReplans, Is.EqualTo(4));
            Assert.That(policy.WaypointTimeout, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(policy.PortalTraversalTimeout, Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(policy.WaypointReachRange, Is.EqualTo(0.4f));
            Assert.That(navigation.InteractWithDoors, Is.True);
            Assert.That(navigation.CanClimb, Is.False);
            Assert.That(navigation.CanPry, Is.False);
            Assert.That(navigation.CanSmash, Is.False);
            Assert.That(stateTree.Rules, Has.Count.EqualTo(10));
            Assert.That(stateTree.BlockedFallback, Is.EqualTo(NCCityPhysicalFallback.ReturnToPublicZone));
            Assert.That(NCCityPhysicalStateTreeSystem.Validate(stateTree), Is.True);
            Assert.That(perception.Radius, Is.EqualTo(10f));
            Assert.That(perception.MaximumObservedEntities, Is.EqualTo(32));
            Assert.That(NCCityLocalPerceptionSystem.Validate(perception), Is.True);
            Assert.That(perceptionPolicy.MaximumUpdatesPerTick, Is.EqualTo(16));
        });
    }

    [Test]
    public void StateTreeConditionsAndInterruptAdaptersAreObjective()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var stateTree = prototypes.Index<NCCityPhysicalStateTreePrototype>(
            new ProtoId<NCCityPhysicalStateTreePrototype>("NCDefaultPhysicalStateTree"));
        var travel = stateTree.Rules.Single(rule => rule.Mode == NCCityPhysicalMode.Traveling);
        var combat = stateTree.Rules.Single(rule =>
            rule.Mode == NCCityPhysicalMode.Combat && rule.RequiredAll.Contains(NCCityPhysicalCondition.CombatActive));
        var conditions = new HashSet<NCCityPhysicalCondition>();

        Assert.That(NCCityPhysicalStateTreeSystem.Matches(travel, conditions), Is.False);
        conditions.Add(NCCityPhysicalCondition.TravelIntent);
        Assert.That(NCCityPhysicalStateTreeSystem.Matches(travel, conditions), Is.True);

        conditions.Clear();
        NCCityPhysicalBehaviorAdapterSystem.AddInterruptConditions(
            NCCityPhysicalInterruptFlags.Combat |
            NCCityPhysicalInterruptFlags.Medical |
            NCCityPhysicalInterruptFlags.Police,
            conditions);
        NCCityPhysicalBehaviorAdapterSystem.AddBlockerConditions(
            NCCityDematerializationBlockerFlags.PlayerTreatment |
            NCCityDematerializationBlockerFlags.Conversation,
            conditions);
        Assert.Multiple(() =>
        {
            Assert.That(NCCityPhysicalStateTreeSystem.Matches(combat, conditions), Is.True);
            Assert.That(conditions, Does.Contain(NCCityPhysicalCondition.MedicalClaim));
            Assert.That(conditions, Does.Contain(NCCityPhysicalCondition.PoliceClaim));
            Assert.That(conditions, Does.Contain(NCCityPhysicalCondition.ConversationClaim));
        });
    }

    [Test]
    public void FormationOffsetAffectsOnlyFinalDestination()
    {
        var network = new NCZNetworkId(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var target = new NCMapCoordinates(network, new System.Numerics.Vector2(10f, 20f), 2);
        var destination = new NCCityNavigationWaypoint(
            NCCityNavigationWaypointKind.Destination,
            target,
            EntityUid.Invalid,
            default,
            default);
        var portal = destination with { Kind = NCCityNavigationWaypointKind.RoomPortalEntry };
        var offset = new System.Numerics.Vector2(2f, -3f);

        var shifted = NCCityGroupTrunkRouteSystem.ApplyFormationOffset(destination, offset);
        var exactPortal = NCCityGroupTrunkRouteSystem.ApplyFormationOffset(portal, offset);

        Assert.Multiple(() =>
        {
            Assert.That(shifted.Target.Position, Is.EqualTo(target.Position + offset));
            Assert.That(shifted.Target.NetworkId, Is.EqualTo(target.NetworkId));
            Assert.That(shifted.Target.Z, Is.EqualTo(target.Z));
            Assert.That(exactPortal.Target, Is.EqualTo(target));
        });
    }

    [Test]
    public void NeutralActionBoundaryRejectsMissingEntitiesWithoutRuleChecks()
    {
        var resolver = Pair.Server.System<NCCityPhysicalActionResolutionSystem>();
        var before = resolver.GetCounters();

        var result = resolver.ResolveActivate(EntityUid.Invalid, EntityUid.Invalid, false);
        var after = resolver.GetCounters();

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCCityPhysicalActionOutcome.Unavailable));
            Assert.That(result.Failure, Is.EqualTo("action-entity-unavailable"));
            Assert.That(after.Attempts, Is.EqualTo(before.Attempts + 1));
            Assert.That(after.Unavailable, Is.EqualTo(before.Unavailable + 1));
        });
    }

    [Test]
    public void DestinationSelectionSeedIsStableAndIntentSensitive()
    {
        var actor = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var destination = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var otherDestination = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeef");

        var first = NCCityPhysicalExecutionSystem.StableSelectionSeed(actor, destination, 7);
        var replay = NCCityPhysicalExecutionSystem.StableSelectionSeed(actor, destination, 7);

        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.EqualTo(first));
            Assert.That(NCCityPhysicalExecutionSystem.StableSelectionSeed(actor, destination, 8), Is.Not.EqualTo(first));
            Assert.That(NCCityPhysicalExecutionSystem.StableSelectionSeed(actor, otherDestination, 7), Is.Not.EqualTo(first));
        });
    }
}
