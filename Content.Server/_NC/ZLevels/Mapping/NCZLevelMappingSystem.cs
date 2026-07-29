/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.ZLevels.Mapping;

public sealed partial class NCZLevelMappingSystem : EntitySystem
{
    [Dependency] private NCZLevelsSystem _zLevels = default!;
    [Dependency] private SharedMapSystem _map = default!;
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCZMapComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCZMapComponent, NCMapAddedIntoZNetworkEvent>(OnAddedIntoZNetwork);
    }

    private void OnAddedIntoZNetwork(Entity<NCZMapComponent> ent, ref NCMapAddedIntoZNetworkEvent args)
    {
        if (_map.IsInitialized(ent))
            EntityManager.AddComponents(ent, args.Network.Comp.Components);
        else
        {
            var hasInitializedMaps = false;
            foreach (var existingMapUid in args.Network.Comp.ZLevels.Values)
            {
                if (existingMapUid.HasValue && _map.IsInitialized(existingMapUid.Value))
                {
                    hasInitializedMaps = true;
                    break;
                }
            }

            if (hasInitializedMaps)
                _map.InitializeMap(ent.Owner);
        }
    }

    private void OnMapInit(Entity<NCZMapComponent> ent, ref MapInitEvent args)
    {
        if (!_zLevels.TryGetMapNetwork(ent, out var network))
            return;

        EntityManager.AddComponents(ent, network.Comp.Components);
    }
}
