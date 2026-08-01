// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.Mapping;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;

namespace Content.Server._NC.Mapping;

/// <summary>
/// Executes authority-sensitive actions requested by the mapper interface.
/// </summary>
public sealed partial class NCMappingHubSystem : EntitySystem
{
    [Dependency] private ISharedAdminManager _admins = default!;
    [Dependency] private NCZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCMappingFloorChangeRequest>(OnFloorChangeRequest);
    }

    private void OnFloorChangeRequest(NCMappingFloorChangeRequest request, EntitySessionEventArgs args)
    {
        if (!_admins.HasAdminFlag(args.SenderSession, AdminFlags.Mapping))
        {
            SendResult(args, false, "Mapping permission is required.", 0);
            return;
        }

        if (args.SenderSession.AttachedEntity is not { } player ||
            Transform(player).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap) ||
            !_zLevels.TryGetMapNetwork(map, out _))
        {
            SendResult(args, false, "You are not currently inside a Z-level network.", 0);
            return;
        }

        var offset = request.TargetDepth - zMap.Depth;
        if (!_zLevels.TryMapOffset((map, zMap), offset, out _))
        {
            SendResult(args, false, $"Floor Z={request.TargetDepth} does not exist.", zMap.Depth);
            return;
        }

        if (request.TargetDepth == zMap.Depth)
        {
            SendResult(args, true, $"Already on floor Z={zMap.Depth}.", zMap.Depth);
            return;
        }

        // NCZLevelsSystem preserves world position and rotation and raises NCZLevelMapMoveEvent.
        if (!_zLevels.TryMove(player, offset, (map, zMap)))
        {
            SendResult(args, false, $"Could not move to floor Z={request.TargetDepth}.", zMap.Depth);
            return;
        }

        SendResult(args, true, $"Moved to floor Z={request.TargetDepth}.", request.TargetDepth);
    }

    private void SendResult(EntitySessionEventArgs args, bool success, string message, int currentDepth)
    {
        RaiseNetworkEvent(
            new NCMappingFloorChangeResult(success, message, currentDepth),
            args.SenderSession);
    }
}
