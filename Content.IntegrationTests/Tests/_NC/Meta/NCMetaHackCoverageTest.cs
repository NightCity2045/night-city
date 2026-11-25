// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Devices;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>
/// Anything with a board that runs META code can be reached and rewritten. A device on a network is reached through its
/// message port; a device that is not wired is reached through the chip in its slot. A prototype that has neither cannot be
/// hacked or reprogrammed by anyone, so it fails here until it is given a way in.
/// </summary>
[TestFixture]
public sealed class NCMetaHackCoverageTest : GameTest
{
    /// <summary>
    /// Implants are configured through META rather than protected by it: what they do is set with a program, and the people
    /// who carry them are not a target for a chip in a slot. The optic controllers are parts of those implants.
    /// </summary>
    private static readonly HashSet<string> ImplantProfiles = ["NCMetaImplant", "NCMetaOpticController"];

    /// <summary>Prototypes that other integration tests register for their own scenes are not content of the game.</summary>
    private const string TestPrototypePrefix = "NCMetaIntegration";

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task EveryMetaDeviceHasAMessagePortOrAChipSlotExceptImplants()
    {
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var components = Server.ResolveDependency<IComponentFactory>();
        var withoutAWayIn = new List<string>();
        var checkedHosts = 0;
        await Server.WaitAssertion(() =>
        {
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(value => !value.Abstract && !value.ID.StartsWith(TestPrototypePrefix, System.StringComparison.Ordinal)))
            {
                if (!prototype.TryGetComponent<NCMetaHostComponent>(out var host, components) || ImplantProfiles.Contains(host.Profile.Id)) continue;
                checkedHosts++;
                var hasPort = prototype.TryGetComponent<NCMetaMessagePortComponent>(out _, components);
                var hasChip = prototype.TryGetComponent<ItemSlotsComponent>(out var slots, components) && slots.Slots.Any(slot => slot.Key == NCMetaChipSlot.Id);
                if (!hasPort && !hasChip) withoutAWayIn.Add(prototype.ID + " (" + host.Profile.Id + ")");
            }
        });
        Assert.That(checkedHosts, Is.GreaterThan(20), "The test found too few META devices to mean anything.");
        Assert.That(withoutAWayIn, Is.Empty, "These META devices cannot be hacked or reprogrammed. Give them NCMetaHackableBase (or a chip slot or " +
            "a message port of their own): " + string.Join(", ", withoutAWayIn.OrderBy(value => value)));
    }

    /// <summary>
    /// Gating is added to the base of every console and vending machine, which are hundreds of prototypes with their own
    /// components. This makes sure each of them can still be created, is known to the device registry and can be removed.
    /// </summary>
    [Test]
    public async Task EveryGatedDeviceCanBeCreatedIsKnownToTheRegistryAndCanBeRemoved()
    {
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var components = Server.ResolveDependency<IComponentFactory>();
        var maps = Server.System<SharedMapSystem>();
        var devices = Server.System<Content.Server._NC.Meta.Devices.NCMetaDeviceRegistrySystem>();
        var problems = new List<string>();
        var created = 0;
        await Server.WaitPost(() =>
        {
            maps.CreateMap(out var map);
            var coordinates = new MapCoordinates(System.Numerics.Vector2.Zero, map);
            foreach (var prototype in prototypes.EnumeratePrototypes<EntityPrototype>().Where(value => !value.Abstract &&
                         !value.ID.StartsWith(TestPrototypePrefix, System.StringComparison.Ordinal) &&
                         value.TryGetComponent<NCMetaGateComponent>(out _, components)).OrderBy(value => value.ID))
            {
                try
                {
                    var entity = SEntMan.SpawnEntity(prototype.ID, coordinates);
                    created++;
                    if (!devices.TryGetEntry(entity, out _)) problems.Add(prototype.ID + ": not known to the device registry");
                    SEntMan.DeleteEntity(entity);
                }
                catch (System.Exception exception) { problems.Add(prototype.ID + ": " + exception.GetType().Name + " " + exception.Message); }
            }
        });
        Assert.That(created, Is.GreaterThan(100), "Far fewer gated devices than the consoles and vending machines of the game.");
        Assert.That(problems, Is.Empty, string.Join(", ", problems.Take(20)));
    }
}
