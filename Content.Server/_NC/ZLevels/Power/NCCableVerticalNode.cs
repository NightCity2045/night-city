/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Nodes;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.NodeContainer;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.ZLevels.Power;

/// <summary>
/// Connects a cable node to matching vertical nodes on adjacent Z-levels.
/// </summary>
[DataDefinition]
public sealed partial class NCCableVerticalNode : Node
{
    [DataField]
    public bool Up;

    [DataField]
    public bool Down;

    public override IEnumerable<Node> GetReachableNodes(
        Entity<TransformComponent> xform,
        EntityQuery<NodeContainerComponent> nodeQuery,
        EntityQuery<TransformComponent> xformQuery,
        Entity<MapGridComponent>? grid,
        IEntityManager entMan)
    {
        if (!xform.Comp.Anchored || grid is not { } gridEnt || xform.Comp.MapUid is null)
            yield break;

        var mapSystem = entMan.System<SharedMapSystem>();
        var zLevels = entMan.System<NCZLevelsSystem>();
        var worldPos = entMan.System<SharedTransformSystem>().GetWorldPosition(xform.Owner);
        var gridIndex = mapSystem.TileIndicesFor(gridEnt, xform.Comp.Coordinates);
        var outputNodes = new List<Node>();

        // A vertical connector remains attached to ordinary cable nodes on its own tile.
        foreach (var node in NodeHelpers.GetNodesInTile(nodeQuery, gridEnt, gridIndex, mapSystem))
        {
            if (node is CableNode)
                outputNodes.Add(node);
        }

        if (Up && zLevels.TryMapUp(xform.Comp.MapUid.Value, out var mapAbove) &&
            mapSystem.TryFindGridAt(mapAbove.Owner, worldPos, out var gridAboveUid, out var gridAboveComp) &&
            mapSystem.TryGetTileRef(gridAboveUid, gridAboveComp, worldPos, out var tileAbove) &&
            !tileAbove.Tile.IsEmpty)
        {
            foreach (var node in NodeHelpers.GetNodesInTile(
                         nodeQuery,
                         (gridAboveUid, gridAboveComp),
                         tileAbove.GridIndices,
                         mapSystem))
            {
                if (node is NCCableVerticalNode { Down: true })
                    outputNodes.Add(node);
            }
        }

        if (Down && zLevels.TryMapDown(xform.Comp.MapUid.Value, out var mapBelow) &&
            mapSystem.TryFindGridAt(mapBelow.Owner, worldPos, out var gridBelowUid, out var gridBelowComp) &&
            mapSystem.TryGetTileRef(gridBelowUid, gridBelowComp, worldPos, out var tileBelow) &&
            !tileBelow.Tile.IsEmpty)
        {
            foreach (var node in NodeHelpers.GetNodesInTile(
                         nodeQuery,
                         (gridBelowUid, gridBelowComp),
                         tileBelow.GridIndices,
                         mapSystem))
            {
                if (node is NCCableVerticalNode { Up: true })
                    outputNodes.Add(node);
            }
        }

        foreach (var node in outputNodes)
            yield return node;
    }
}
