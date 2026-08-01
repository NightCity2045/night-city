// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Mapping;

namespace Content.Client._NC.Mapping;

/// <summary>
/// Provides the mapper UI with a narrow request API and authoritative operation results.
/// </summary>
public sealed partial class NCMappingHubClientSystem : EntitySystem
{
    public event Action<NCMappingFloorChangeResult>? FloorChangeCompleted;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCMappingFloorChangeResult>(OnFloorChangeResult);
    }

    public void RequestFloorChange(int targetDepth)
    {
        RaiseNetworkEvent(new NCMappingFloorChangeRequest(targetDepth));
    }

    private void OnFloorChangeResult(NCMappingFloorChangeResult result)
    {
        FloorChangeCompleted?.Invoke(result);
    }
}
