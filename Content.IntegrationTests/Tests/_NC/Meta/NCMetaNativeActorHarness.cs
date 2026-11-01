// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading.Tasks;
using System.Collections.Generic;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Sets up a real resident's cached domains and physical inventory before binding a fixture body.</summary>
internal static class NCMetaNativeActorHarness
{
    public static async Task UnbindBeforeDelete(RobustIntegrationTest.ServerIntegrationInstance server, params EntityUid[] bodies)
    {
        var barriers = new List<Task>();
        await server.WaitAssertion(() =>
        {
            var runtime = server.System<NCCityActorRuntimeIndexSystem>();
            var checkpoints = server.System<NCPhysicalActorCheckpointSystem>();
            foreach (var body in bodies)
            {
                if (!runtime.TryGetActor(body, out var actor)) continue;
                Assert.That(runtime.Unbind(body), Is.True);
                barriers.Add(checkpoints.GetRecoveryBarrier(actor));
            }
        });
        await NCMetaNativeTestTasks.Run(server, async () => { await Task.WhenAll(barriers); return true; });
    }

    public static async Task RestoreBinding(RobustIntegrationTest.ServerIntegrationInstance server, NCActorId actor, EntityUid body)
    {
        var domains = server.System<NCCityActorDomainRegistrySystem>();
        Assert.That(await NCMetaNativeTestTasks.Run(server, () => server.System<NCCityActorRegistrySystem>().GetAsync(actor)), Is.Not.Null);
        var health = await NCMetaNativeTestTasks.Run(server, () => domains.GetHealthAsync(actor));
        var inventory = await NCMetaNativeTestTasks.Run(server, () => domains.GetInventoryAsync(actor));
        Assert.That(health, Is.Not.Null); Assert.That(inventory, Is.Not.Null);
        var physicalization = server.System<NCEconomicPhysicalizationSystem>();
        var plan = await NCMetaNativeTestTasks.Run(server, () => physicalization.BuildActorInventoryPlanAsync(actor, inventory!.Snapshot));
        Assert.That(plan.IsSuccess, Is.True, plan.Error);
        await server.WaitAssertion(() =>
        {
            var manager = server.ResolveDependency<IEntityManager>();
            var adapter = manager.EnsureComponent<NCCityActorInventoryAdapterComponent>(body);
            adapter.AuthorityVersion = inventory!.Snapshot.SourceAuthorityVersion;
            adapter.EquippedLoadoutId = inventory.Snapshot.Summary.EquippedLoadoutId;
            Assert.That(physicalization.TryMaterialize(body, plan.Plan!, out var error), Is.True, error);
            Assert.That(server.System<NCCityActorRuntimeIndexSystem>().TryBind(actor, body), Is.True);
            Assert.That(server.System<NCActiveWorldTimeSystem>().TryGetCurrentTime(out _, out var now), Is.True);
            Assert.That(domains.TryCaptureMaterialized(actor, body, health!.AggregateVersion, inventory.AggregateVersion, now, out _, out error), Is.True, error);
        });
    }
}
