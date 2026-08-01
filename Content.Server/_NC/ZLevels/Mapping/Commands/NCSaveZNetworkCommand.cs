/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server.Administration;
// NC start: persist the stable identity of saved Z-networks.
using Content.Server._NC.City.Zones.Mapping;
using Content.Shared._NC.Coordinates.Components;
// NC end
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared.Administration;
using Robust.Server.GameObjects;
using Robust.Shared.Console;
// NC start: write the Z-network save manifest to UserData.
using Robust.Shared.ContentPack;
// NC end
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.Server._NC.ZLevels.Mapping.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCSaveZNetworkCommand : LocalizedEntityCommands
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private MapSystem _map = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    // NC start: save NCZNetworkId beside the network maps.
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private NCZoneEditorServerSystem _zoneEditor = default!;
    // NC end

    public override string Command => "znetwork-save";
    public override string Description => "Save all zNetwork maps to default server folder";

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var options = new List<CompletionOption>();
            var query = _entities.EntityQueryEnumerator<NCZMapNetworkComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out _, out var meta))
            {
                options.Add(new CompletionOption(_entities.GetNetEntity(uid).ToString(), meta.EntityName));
            }
            return CompletionResult.FromHintOptions(options, "zNetwork net entity");
        }
        if (args.Length == 2)
        {
            return CompletionResult.FromHint("ZNetwork name (for example: `Dev`)");
        }
        return CompletionResult.Empty;
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError("Wrong arguments count.");
            return;
        }

        // NC start: prevent paths outside ZNetworkSaves and require persistent identity metadata.
        var saveName = args[1];
        if (!NCZNetworkSaveManifest.IsValidSaveName(saveName))
        {
            shell.WriteError("The save folder name is invalid.");
            return;
        }
        // NC end

        // get the target
        EntityUid? target;

        if (!NetEntity.TryParse(args[0], out var targetNet) ||
            !_entities.TryGetEntity(targetNet, out target))
        {
            shell.WriteError($"Unable to find entity {args[1]}");
            return;
        }

        if (!_entities.TryGetComponent<NCZMapNetworkComponent>(target, out var levelComp))
        {
            shell.WriteError($"Target entity doesnt have NCZLevelsNetworkComponent {args[1]}");
            return;
        }

        // NC start: NCMapCoordinates require this identity to survive a save/load cycle.
        if (!_entities.TryGetComponent<NCZNetworkIdentityComponent>(target, out var identity) ||
            !identity.NetworkId.IsValid)
        {
            shell.WriteError("Target Z-network does not have a valid NCZNetworkId.");
            return;
        }

        var saveFolder = new ResPath("/ZNetworkSaves") / saveName;
        var allMapsSaved = true;

        if (levelComp.ZLevels.Count == 0)
        {
            shell.WriteError("The Z-network has no maps to save.");
            return;
        }

        // Validate the mapper's current zone draft before overwriting any map in the bundle.
        if (!_zoneEditor.TryValidateNetworkSave(
                shell.Player?.UserId,
                identity.NetworkId,
                out var hasZones,
                out var zoneError))
        {
            shell.WriteError(zoneError ?? "The zone draft cannot be saved.");
            return;
        }
        // NC end

        foreach (var (depth, mapUid) in levelComp.ZLevels)
        {
            if (!_entities.TryGetComponent<MapComponent>(mapUid, out var mapComp))
            {
                shell.WriteError($"Map entity {mapUid} doesnt have MapComponent.");
                // NC start: an incomplete map set must not publish fresh network or zone metadata.
                allMapsSaved = false;
                // NC end
                continue;
            }

            var mapId = mapComp.MapId;

            // no saving null space
            if (mapId == MapId.Nullspace)
                return;

            if (!_map.MapExists(mapId))
            {
                shell.WriteError($"Map {mapId} doesnt exist!");
                return;
            }

            if (_map.IsInitialized(mapId))
            {
                shell.WriteError($"Map {mapId} is already initialized, cannot save initialized maps!");
                return;
            }

            // NC start: use the validated name and shared save folder.
            var savePath = saveFolder / $"{saveName}{depth}.yml";
            // NC end
            shell.WriteLine(Loc.GetString("cmd-savemap-attempt", ("mapId", mapId), ("path", savePath)));
            if (_mapLoader.TrySaveMap(mapId, savePath))
            {
                shell.WriteLine(Loc.GetString("cmd-savemap-success"));
            }
            else
            {
                shell.WriteError(Loc.GetString("cmd-savemap-error"));
                // NC start: do not publish new identity metadata for an incomplete save.
                allMapsSaved = false;
                // NC end
            }
        }

        // NC start: publish the sidecars only after every map was written successfully.
        if (!allMapsSaved)
            return;

        if (hasZones)
        {
            var zonePath = saveFolder / NCZoneEditorServerSystem.NetworkSaveFileName;
            if (!_zoneEditor.TrySaveNetworkZones(
                    shell.Player?.UserId,
                    identity.NetworkId,
                    zonePath,
                    out zoneError))
            {
                shell.WriteError(zoneError ?? "Unable to save the Z-network zone set.");
                return;
            }

            shell.WriteLine($"Saved city zones to {zonePath}.");
        }
        else
        {
            var zonePath = saveFolder / NCZoneEditorServerSystem.NetworkSaveFileName;
            shell.WriteLine(_resources.UserData.Exists(zonePath)
                ? $"No current zone source belongs to this network; existing {zonePath} was preserved."
                : "No zone draft or loaded zone set belongs to this network; no zone sidecar was written.");
        }

        NCZNetworkSaveManifest.Write(_resources.UserData, saveFolder, identity.NetworkId);
        // NC end
    }
}
