// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Coordinates.Components;

/// <summary>
/// Data-only persistent identity attached to the runtime entity of a Z-level network.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCZNetworkIdentityComponent : Component
{
    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    [AutoNetworkedField]
    public NCZNetworkId NetworkId;
}
