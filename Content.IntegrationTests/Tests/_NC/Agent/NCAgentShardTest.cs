// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.Agent.Shards;
using Content.Shared._NC.Agent.Shards.MedTek;
using Content.Shared._NC.Agent.Shards.Notes;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PDA;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Agent;

[TestFixture]
[TestOf(typeof(NCAgentShardSystem))]
public sealed class NCAgentShardTest : GameTest
{
    [Test]
    public async Task ProtectedShardUsesCurrentVinAccess()
    {
        var pair = Pair;
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var agent = EntityUid.Invalid;
        var shard = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            agent = server.EntMan.SpawnEntity("PassengerPDA", map.GridCoords);
            shard = server.EntMan.SpawnEntity("WantedListCartridge", map.GridCoords);
        });
        await pair.RunTicksSync(1);

        await server.WaitPost(() =>
        {
            var shardSystem = server.System<NCAgentShardSystem>();
            var cartridges = server.System<CartridgeLoaderSystem>();
            var slots = server.System<ItemSlotsSystem>();
            var loader = server.EntMan.GetComponent<CartridgeLoaderComponent>(agent);
            var pda = server.EntMan.GetComponent<PdaComponent>(agent);

            Assert.That(slots.TryInsert(agent, loader.CartridgeSlot, shard, null), Is.True);
            Assert.That(cartridges.InstallCartridge((agent, loader), shard), Is.True);
            Assert.That(shardSystem.IsAuthorized(agent, shard), Is.False);

            cartridges.ActivateProgram((agent, loader), shard);
            Assert.That(loader.ActiveProgram, Is.Null);

            Assert.That(pda.ContainedId, Is.Not.Null);
            var vinAccess = server.EntMan.GetComponent<AccessComponent>(pda.ContainedId!.Value);
            vinAccess.Tags.Add(new ProtoId<AccessLevelPrototype>("NCPD_Patrol"));

            Assert.That(shardSystem.IsAuthorized(agent, shard), Is.True);
            cartridges.ActivateProgram((agent, loader), shard);
            Assert.That(loader.ActiveProgram, Is.EqualTo(shard));

            server.EntMan.DeleteEntity(agent);
        });
    }

    [Test]
    public async Task PhysicalShardKeepsIdentityAcrossInstallAndRemove()
    {
        var pair = Pair;
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var agent = EntityUid.Invalid;
        var shard = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            agent = server.EntMan.SpawnEntity("PassengerPDA", map.GridCoords);
            shard = server.EntMan.SpawnEntity("AstroNavCartridge", map.GridCoords);
        });
        await pair.RunTicksSync(1);

        await server.WaitPost(() =>
        {
            var cartridges = server.System<CartridgeLoaderSystem>();
            var slots = server.System<ItemSlotsSystem>();
            var loader = server.EntMan.GetComponent<CartridgeLoaderComponent>(agent);

            Assert.That(slots.TryInsert(agent, loader.CartridgeSlot, shard, null), Is.True);
            Assert.That(cartridges.InstallCartridge((agent, loader), shard), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(cartridges.GetRemovablePrograms(agent), Does.Contain(shard));
                Assert.That(server.EntMan.EntityExists(shard), Is.True);
                Assert.That(server.EntMan.GetComponent<CartridgeComponent>(shard).InstallationStatus,
                    Is.EqualTo(InstallationStatus.Installed));
            });

            Assert.That(cartridges.UninstallProgram((agent, loader), shard), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(loader.CartridgeSlot.Item, Is.EqualTo(shard));
                Assert.That(server.EntMan.EntityExists(shard), Is.True);
                Assert.That(server.EntMan.GetComponent<CartridgeComponent>(shard).InstallationStatus,
                    Is.EqualTo(InstallationStatus.Cartridge));
            });

            server.EntMan.DeleteEntity(agent);
        });
    }

    [Test]
    public async Task WeightedMemoryRejectsOverflow()
    {
        var pair = Pair;
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var agent = EntityUid.Invalid;
        var patient = EntityUid.Invalid;

        await server.WaitPost(() =>
        {
            agent = server.EntMan.SpawnEntity("PassengerPDA", map.GridCoords);
            patient = server.EntMan.SpawnEntity("MobHuman", map.GridCoords);
        });
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
                Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(4));
            });

            // Base firmware is NOTES + TASKBOARD (4U); three distinct specialist shards fill the 16U budget.
            Assert.That(cartridges.InstallProgram((agent, loader), "NetProbeCartridge"), Is.True);
            Assert.That(cartridges.InstallProgram((agent, loader), "LogProbeCartridge"), Is.True);
            Assert.That(cartridges.InstallProgram((agent, loader), "MedTekCartridge"), Is.True);
            Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(16));
            Assert.That(cartridges.InstallProgram((agent, loader), "AstroNavCartridge"), Is.False);
            Assert.That(shards.GetUsedMemory(agent), Is.EqualTo(16));

            var medTek = cartridges.TryGetProgram<NCAgentMedTekComponent>((agent, loader));
            Assert.That(medTek.HasValue, Is.True);
            var analyzer = server.EntMan.SpawnEntity("HandheldHealthAnalyzer", map.GridCoords);
            var scan = new NCAgentMedicalScanCompletedEvent(agent, patient);
            server.EntMan.EventBus.RaiseLocalEvent(analyzer, ref scan);
            Assert.Multiple(() =>
            {
                Assert.That(medTek!.Value.Comp.History, Has.Count.EqualTo(1));
                Assert.That(medTek.Value.Comp.History[0].TargetName, Is.Not.Empty);
            });

            var taskboard = cartridges.TryGetProgram<NanoTaskCartridgeComponent>((agent, loader));
            Assert.That(taskboard.HasValue, Is.True);
            var addTask = new NanoTaskUiMessageEvent(new NanoTaskAddTask(new NanoTaskItem(
                "Meet fixer",
                "Afterlife",
                false,
                NanoTaskPriority.High,
                NanoTaskStatus.Active,
                "23:30")))
            {
                Actor = agent,
                LoaderUid = server.EntMan.GetNetEntity(agent),
            };
            CartridgeMessageEvent taskMessage = addTask;
            server.EntMan.EventBus.RaiseLocalEvent(taskboard!.Value.Owner, taskMessage);
            Assert.Multiple(() =>
            {
                Assert.That(taskboard.Value.Comp.Tasks, Has.Count.EqualTo(1));
                Assert.That(taskboard.Value.Comp.Tasks[0].Data.Status, Is.EqualTo(NanoTaskStatus.Active));
                Assert.That(taskboard.Value.Comp.Tasks[0].Data.Deadline, Is.EqualTo("23:30"));
            });

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
            server.EntMan.DeleteEntity(patient);
        });
    }
}
