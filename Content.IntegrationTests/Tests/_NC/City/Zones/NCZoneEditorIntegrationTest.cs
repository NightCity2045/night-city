// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._NC.City.Zones;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.City.Zones;
using Content.Server._NC.City.Zones.Mapping;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Editor;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Client.Input;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NC.City.Zones;

[TestFixture]
[TestOf(typeof(NCZoneEditorClientSystem))]
public sealed class NCZoneEditorIntegrationTest : GameTest
{
    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("ca873cdb-6f18-4f37-8eed-1a186253684c"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
    };

    [Test]
    public async Task AuthorizedMapperCanBootstrapEditUndoAndValidate()
    {
        var pair = Pair;
        var server = pair.Server;
        var client = pair.Client;
        var maps = server.System<SharedMapSystem>();
        var transforms = server.System<SharedTransformSystem>();
        var zLevels = server.System<NCZLevelsSystem>();
        var editor = client.System<NCZoneEditorClientSystem>();

        Entity<NCZMapNetworkComponent> network = default;
        MapId mapId = default;
        MapId upperMapId = default;
        await server.WaitPost(() =>
        {
            maps.CreateMap(out mapId);
            maps.CreateMap(out upperMapId);
            network = zLevels.CreateMapNetwork(NetworkId);
            Assert.That(
                zLevels.TryAddMapsIntoNetwork(
                    network,
                    new Dictionary<EntityUid, int>
                    {
                        [maps.GetMap(mapId)] = 0,
                        [maps.GetMap(upperMapId)] = 1,
                    }),
                Is.True);

            transforms.SetMapCoordinates(
                pair.Player!.AttachedEntity!.Value,
                new MapCoordinates(Vector2.Zero, mapId));
        });
        await pair.RunTicksSync(5);

        await client.WaitPost(() => editor.Open());
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            Assert.That(editor.Snapshot, Is.Not.Null);
            Assert.That(editor.Snapshot!.NetworkId, Is.EqualTo(NetworkId));
            Assert.That(editor.Snapshot.Zones, Is.Empty);
        });

        var originalCount = editor.Snapshot!.Zones.Length;
        await client.WaitPost(() =>
            editor.CreateZone("District", "Editor District", default));
        await pair.RunTicksSync(5);

        NCZoneId createdId = default;
        await client.WaitAssertion(() =>
        {
            var created = editor.Snapshot!.Zones
                .SingleOrDefault(zone => zone.Name == "Editor District");
            Assert.That(created, Is.Not.Null);
            createdId = created!.Id;
            Assert.That(createdId.IsValid, Is.True);
            Assert.That(editor.SelectedZone, Is.EqualTo(createdId));
            Assert.That(editor.Snapshot.Zones.Length, Is.EqualTo(originalCount + 1));
            Assert.That(editor.Snapshot.Dirty, Is.True);
        });

        var serverEditor = server.System<NCZoneEditorServerSystem>();
        await server.WaitAssertion(() =>
        {
            Assert.That(
                serverEditor.TryExportDraft(pair.Player!.UserId, "invalid-empty-zone", out _),
                Is.False,
                "Console/API export must reject a draft before its zone has geometry.");
            Assert.That(
                serverEditor.TryValidateNetworkSave(
                    pair.Player.UserId,
                    NetworkId,
                    out var hasInvalidZones,
                    out _),
                Is.False,
                "znetwork-save preflight must reject an invalid draft before overwriting map files.");
            Assert.That(hasInvalidZones, Is.True);
        });

        var input = client.ResolveDependency<IInputManager>();
        await client.WaitPost(() =>
        {
            Assert.That(editor.Select(createdId), Is.True);
            Assert.That(
                editor.StartPolygon(NCZoneGeometryKind.Polygon, 0, 0, 0, 1f),
                Is.True);
            Assert.That(input.Contexts.ActiveContext.Name, Is.EqualTo("editor"));

            editor.CancelTool();
            Assert.That(input.Contexts.ActiveContext.Name, Is.Not.EqualTo("editor"));
        });

        await client.WaitPost(() =>
            editor.SubmitGeometry(
                createdId,
                NCZoneGeometryKind.Polygon,
                0,
                0,
                0,
                [
                    new Vector2(20, 20),
                    new Vector2(24, 20),
                    new Vector2(24, 24),
                    new Vector2(20, 24),
                    // Mappers commonly close a polygon by clicking its first vertex again.
                    new Vector2(20, 20),
                ],
                []));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var created = editor.Snapshot!.Zones.Single(zone => zone.Id == createdId);
            Assert.That(created.Geometry, Has.Length.EqualTo(1));
            Assert.That(created.Geometry[0].Kind, Is.EqualTo(NCZoneGeometryKind.Polygon));
            Assert.That(created.Geometry[0].Vertices, Has.Length.EqualTo(4));
        });

        await client.WaitPost(() =>
            editor.InsertVertex(createdId, 0, 0, new Vector2(22, 19)));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var polygon = editor.Snapshot!.Zones
                .Single(zone => zone.Id == createdId)
                .Geometry[0];
            Assert.That(polygon.Vertices, Has.Length.EqualTo(5));
            Assert.That(polygon.Vertices[1], Is.EqualTo(new Vector2(22, 19)));
        });

        await client.WaitPost(editor.ValidateDraft);
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
            Assert.That(editor.ValidationErrors, Is.Empty));

        await client.WaitPost(editor.Undo);
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
            Assert.That(
                editor.Snapshot!.Zones.Single(zone => zone.Id == createdId).Geometry[0].Vertices,
                Has.Length.EqualTo(4)));

        await client.WaitPost(editor.Redo);
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
            Assert.That(
                editor.Snapshot!.Zones.Single(zone => zone.Id == createdId).Geometry[0].Vertices,
                Has.Length.EqualTo(5)));

        // A range may include planned floors that are not loaded yet (only Z=0 and Z=1 exist here).
        await client.WaitPost(() =>
            editor.SetGeometryScope(createdId, 0, false, 0, 5));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var geometry = editor.Snapshot!.Zones
                .Single(zone => zone.Id == createdId)
                .Geometry[0];
            Assert.Multiple(() =>
            {
                Assert.That(geometry.Kind, Is.EqualTo(NCZoneGeometryKind.Volume));
                Assert.That(geometry.Global, Is.False);
                Assert.That(geometry.MinZ, Is.Zero);
                Assert.That(geometry.MaxZ, Is.EqualTo(5));
            });
        });

        // Global geometry deliberately ignores loaded-floor bounds and survives YAML export.
        await client.WaitPost(() =>
            editor.SetGeometryScope(createdId, 0, true, 0, 1));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            var geometry = editor.Snapshot!.Zones
                .Single(zone => zone.Id == createdId)
                .Geometry[0];
            Assert.That(geometry.Global, Is.True);
        });

        var exportPath = new ResPath("/ZoneExports/nc-zone-editor-integration.yml");
        await server.WaitAssertion(() =>
        {
            if (server.ResolveDependency<IResourceManager>().UserData.Exists(exportPath))
            {
                server.ResolveDependency<IResourceManager>().UserData.Delete(exportPath);
            }

            Assert.That(
                serverEditor.TryExportDraft(pair.Player!.UserId, "nc-zone-editor-integration", out var actualPath),
                Is.True);
            Assert.That(actualPath, Is.EqualTo(exportPath));
            var yaml = server.ResolveDependency<IResourceManager>().UserData.ReadAllText(exportPath);
            Assert.That(yaml, Does.Contain("global: true"));
        });

        await server.WaitPost(() =>
        {
            var resources = server.ResolveDependency<IResourceManager>();
            if (resources.UserData.Exists(exportPath))
                resources.UserData.Delete(exportPath);
        });

        var bundlePath = new ResPath("/ZNetworkSaves/nc-zone-editor-integration") /
                         NCZoneEditorServerSystem.NetworkSaveFileName;
        await server.WaitAssertion(() =>
        {
            var resources = server.ResolveDependency<IResourceManager>();
            if (resources.UserData.Exists(bundlePath))
                resources.UserData.Delete(bundlePath);

            Assert.That(
                serverEditor.TryValidateNetworkSave(
                    pair.Player!.UserId,
                    NetworkId,
                    out var hasZones,
                    out var validationError),
                Is.True,
                validationError);
            Assert.That(hasZones, Is.True);
            Assert.That(
                serverEditor.TrySaveNetworkZones(
                    pair.Player.UserId,
                    NetworkId,
                    bundlePath,
                    out var saveError),
                Is.True,
                saveError);
            Assert.That(resources.UserData.Exists(bundlePath), Is.True);

            var wrongNetwork = new NCZNetworkId(Guid.Parse("0f192555-00e0-4f42-a019-bf0cf8ab58da"));
            Assert.That(
                serverEditor.TryLoadNetworkZones(bundlePath, wrongNetwork, out _),
                Is.False,
                "A zone sidecar from another persistent Z-network must be rejected.");
            Assert.That(
                serverEditor.TryLoadNetworkZones(bundlePath, NetworkId, out var loadError),
                Is.True,
                loadError);

            var zoneSetId = $"NCZones{NetworkId.Value:N}";
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypes.TryIndex<NCZoneSetPrototype>(zoneSetId, out var restored), Is.True);
            Assert.That(restored!.Zones, Has.Count.EqualTo(1));

            var zones = server.System<NCZoneSystem>();
            var result = new List<NCZoneInfo>();
            zones.GetZones(new NCMapCoordinates(NetworkId, new Vector2(22, 22), 0), result);
            Assert.That(result.Select(zone => zone.Name), Does.Contain("Editor District"));
        });

        await server.WaitPost(() =>
        {
            var resources = server.ResolveDependency<IResourceManager>();
            if (resources.UserData.Exists(bundlePath))
                resources.UserData.Delete(bundlePath);
        });
        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await pair.RunTicksSync(1);
    }
}
