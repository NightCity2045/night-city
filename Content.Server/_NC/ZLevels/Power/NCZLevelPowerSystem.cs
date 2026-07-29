/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server.NodeContainer.EntitySystems;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.NodeContainer;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.ZLevels.Power;

/// <summary>
/// Rebuilds vertical power connections when grids join or leave a Z-grid network.
/// </summary>
public sealed class NCZLevelPowerSystem : EntitySystem
{
    [Dependency] private NodeGroupSystem _nodeGroup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGridComponent, NCGridAddedIntoZNetworkEvent>(OnGridLinked);
        SubscribeLocalEvent<MapGridComponent, NCGridRemovedFromZNetworkEvent>(OnGridUnlinked);
    }

    private void OnGridLinked(Entity<MapGridComponent> grid, ref NCGridAddedIntoZNetworkEvent args)
    {
        RefloodVerticalNodes(grid.Owner);
    }

    private void OnGridUnlinked(Entity<MapGridComponent> grid, ref NCGridRemovedFromZNetworkEvent args)
    {
        RefloodVerticalNodes(grid.Owner);
    }

    private void RefloodVerticalNodes(EntityUid gridUid)
    {
        // Network topology changes are rare; scan only on those events and never per tick.
        var query = EntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
        while (query.MoveNext(out _, out var container, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            foreach (var node in container.Nodes.Values)
            {
                if (node is NCCableVerticalNode)
                    _nodeGroup.QueueReflood(node);
            }
        }
    }
}
