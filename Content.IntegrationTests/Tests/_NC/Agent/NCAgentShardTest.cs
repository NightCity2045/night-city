// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.Agent.Shards;
using Content.Shared._NC.Agent.Shards.Notes;
using Content.Shared.CartridgeLoader;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Agent;

[TestFixture]
[TestOf(typeof(NCAgentShardSystem))]
public sealed class NCAgentShardTest : GameTest
{
    [Test]
    public async Task WeightedMemoryRejectsOverflow()
    {
        var pair = Pair;
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var agent = EntityUid.Invalid;

        await server.WaitPost(() => agent = server.EntMan.SpawnEntity("PassengerPDA", map.GridCoords));
        await pair.RunTicksSync(1);

        await server.WaitPost(() =>
        {
            var shards = server.System<NCAgentShardSystem>();
            var cartridges = server.System<CartridgeLoaderSystem>();
            var loader = server.EntMan.GetComponent<CartridgeLoaderComponent>(agent);
            var storage = server.EntMan.GetComponent<NCAgentShardStorageComponent>(agent);

            Assert.Multiple(() =>
            {
                Assert.That(storage.Capacity, Is.EqualTo(16));
                Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(10));
            });

            // The default Agent image uses 10U. One 4U SCANNER fits; a second one would cross the 16U budget.
            Assert.That(cartridges.InstallProgram((agent, loader), "NetProbeCartridge"), Is.True);
            Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(14));
            Assert.That(cartridges.InstallProgram((agent, loader), "NetProbeCartridge"), Is.False);
            Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(14));

            var notes = cartridges.TryGetProgram<NCAgentNotesComponent>((agent, loader));
            Assert.That(notes.HasValue, Is.True);
            var noteUid = notes!.Value.Owner;
            var add = new NCAgentNotesUiMessage(NCAgentNotesAction.Add, Guid.Empty, "Meet fixer at 23:00")
            {
                Actor = agent,
                LoaderUid = server.EntMan.GetNetEntity(agent),
            };
            // CartridgeLoaderSystem relays derived program messages through their CartridgeMessageEvent base type.
            CartridgeMessageEvent relayed = add;
            server.EntMan.EventBus.RaiseLocalEvent(noteUid, relayed);
            Assert.That(notes.Value.Comp.Entries, Has.Count.EqualTo(1));
            Assert.That(notes.Value.Comp.Entries[0].Body, Is.EqualTo("Meet fixer at 23:00"));

            var noteId = notes.Value.Comp.Entries[0].Id;
            var edit = new NCAgentNotesUiMessage(NCAgentNotesAction.Edit, noteId, "Meet fixer at 23:30")
            {
                Actor = agent,
                LoaderUid = server.EntMan.GetNetEntity(agent),
            };
            relayed = edit;
            server.EntMan.EventBus.RaiseLocalEvent(noteUid, relayed);
            Assert.That(notes.Value.Comp.Entries[0].Body, Is.EqualTo("Meet fixer at 23:30"));

            var delete = new NCAgentNotesUiMessage(NCAgentNotesAction.Delete, noteId, string.Empty)
            {
                Actor = agent,
                LoaderUid = server.EntMan.GetNetEntity(agent),
            };
            relayed = delete;
            server.EntMan.EventBus.RaiseLocalEvent(noteUid, relayed);
            Assert.That(notes.Value.Comp.Entries, Is.Empty);

            server.EntMan.DeleteEntity(agent);
        });
    }
}
