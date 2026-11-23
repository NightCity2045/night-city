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

    /// <summary>A native owner is an actor bound to a body; there is no resident record to restore.</summary>
    public static Task RestoreBinding(RobustIntegrationTest.ServerIntegrationInstance server, NCActorId actor, EntityUid body)
    {
        return server.WaitAssertion(() => Assert.That(server.System<NCCityActorRuntimeIndexSystem>().TryBind(actor, body), Is.True));
    }
}
