// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text.RegularExpressions;
using Content.Server._NC.City.Zones.Mapping;
using Content.Server._NC.ZLevels.Core;
using Content.Server.Administration;
using Content.Shared._NC.Coordinates;
using Content.Shared.Administration;
using Robust.Server.GameObjects;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.Server._NC.ZLevels.Mapping.Commands;

/// <summary>
/// Loads every map produced by znetwork-save and reconnects them into one Z-level network.
/// </summary>
[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCLoadZNetworkCommand : LocalizedEntityCommands
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private NCZLevelsSystem _zLevels = default!;
    [Dependency] private NCZoneEditorServerSystem _zoneEditor = default!;
    [Dependency] private MetaDataSystem _meta = default!;

    public override string Command => "znetwork-load";
    public override string Description => "Load all Z-network maps from a saved folder.";
    public override string Help => "znetwork-load <folder name>";

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var savesPath = new ResPath("/ZNetworkSaves");
        if (!_resources.UserData.IsDir(savesPath))
            return CompletionResult.Empty;

        var options = _resources.UserData.DirectoryEntries(savesPath)
            .Where(entry => _resources.UserData.IsDir(savesPath / entry))
            .Select(entry => new CompletionOption(entry))
            .ToList();

        return CompletionResult.FromHintOptions(options, "Save folder name");
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        var saveName = args[0];
        if (!NCZNetworkSaveManifest.IsValidSaveName(saveName))
        {
            shell.WriteError("The save folder name is invalid.");
            return;
        }

        var folder = new ResPath("/ZNetworkSaves") / saveName;
        if (!_resources.UserData.IsDir(folder))
        {
            shell.WriteError($"Directory {folder} does not exist in UserData.");
            return;
        }

        if (!TryFindMaps(shell, folder, saveName, out var mapsToLoad))
            return;

        // New saves preserve their identity in the manifest. Legacy saves remain loadable,
        // but receive an identity once and immediately persist it for all subsequent loads.
        NCZNetworkId networkId;
        if (!NCZNetworkSaveManifest.TryRead(_resources.UserData, folder, out networkId, out var manifestError))
        {
            if (manifestError != null)
            {
                shell.WriteError(manifestError);
                return;
            }

            networkId = new NCZNetworkId(Guid.NewGuid());
            NCZNetworkSaveManifest.Write(_resources.UserData, folder, networkId);
            shell.WriteLine(
                $"Legacy save has no manifest. Generated and stored NCZNetworkId {networkId}.");
        }

        var loadedMaps = new Dictionary<EntityUid, int>();
        var options = new DeserializationOptions { StoreYamlUids = true };

        foreach (var (depth, path) in mapsToLoad)
        {
            shell.WriteLine($"Loading map for depth {depth} from {path}...");

            if (!_mapLoader.TryLoadMap(path, out var mapEntity, out _, options))
            {
                shell.WriteError($"Failed to load map from {path}. No Z-network was created.");
                DeleteLoadedMaps(loadedMaps.Keys);
                return;
            }

            var mapUid = mapEntity.Value.Owner;
            if (!_entities.HasComponent<MapComponent>(mapUid))
            {
                shell.WriteError($"Loaded entity {mapUid} does not have MapComponent.");
                _entities.QueueDeleteEntity(mapUid);
                DeleteLoadedMaps(loadedMaps.Keys);
                return;
            }

            loadedMaps.Add(mapUid, depth);
            _meta.SetEntityName(mapUid, $"{saveName} [{depth}]");
        }

        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network;
        try
        {
            network = _zLevels.CreateMapNetwork(networkId);
        }
        catch (InvalidOperationException exception)
        {
            DeleteLoadedMaps(loadedMaps.Keys);
            shell.WriteError($"Unable to create Z-network: {exception.Message}");
            return;
        }

        _meta.SetEntityName(network, $"z-Network: {saveName}");

        if (!_zLevels.TryAddMapsIntoNetwork(network, loadedMaps))
        {
            // DeleteMapNetwork also owns cleanup of every map already attached to the failed network.
            _zLevels.DeleteMapNetwork(network);
            shell.WriteError("Failed to attach the loaded maps to the Z-network.");
            return;
        }

        var zonePath = folder / NCZoneEditorServerSystem.NetworkSaveFileName;
        if (_resources.UserData.Exists(zonePath))
        {
            if (!_zoneEditor.TryLoadNetworkZones(zonePath, networkId, out var zoneError))
            {
                _zLevels.DeleteMapNetwork(network);
                shell.WriteError(zoneError ?? $"Unable to load city zones from {zonePath}.");
                return;
            }

            shell.WriteLine($"Loaded city zones from {zonePath}.");
        }
        else
        {
            shell.WriteLine("This legacy Z-network save has no _zones.yml sidecar.");
        }

        shell.WriteLine($"Successfully loaded Z-network '{saveName}' with {loadedMaps.Count} maps.");
        shell.WriteLine($"NCZNetworkId: {networkId}");
        shell.WriteLine($"ZNetwork Entity: {_entities.GetNetEntity(network)}");
        shell.WriteLine("Use 'znetwork-initialize' to initialize the maps when ready.");
    }

    private bool TryFindMaps(
        IConsoleShell shell,
        ResPath folder,
        string saveName,
        out SortedDictionary<int, ResPath> maps)
    {
        maps = new SortedDictionary<int, ResPath>();
        var pattern = new Regex(
            $@"^{Regex.Escape(saveName)}(?<depth>-?\d+)\.yml$",
            RegexOptions.CultureInvariant);

        foreach (var entry in _resources.UserData.DirectoryEntries(folder))
        {
            var match = pattern.Match(entry);
            if (!match.Success ||
                !int.TryParse(match.Groups["depth"].Value, out var depth))
            {
                continue;
            }

            if (!maps.TryAdd(depth, folder / entry))
            {
                shell.WriteError($"Multiple map files resolve to Z-depth {depth} in {folder}.");
                return false;
            }
        }

        if (maps.Count != 0)
            return true;

        shell.WriteError(
            $"No Z-network map files matching {saveName}{{depth}}.yml were found in {folder}.");
        return false;
    }

    private void DeleteLoadedMaps(IEnumerable<EntityUid> maps)
    {
        foreach (var map in maps)
        {
            if (!_entities.Deleted(map))
                _entities.QueueDeleteEntity(map);
        }
    }
}
