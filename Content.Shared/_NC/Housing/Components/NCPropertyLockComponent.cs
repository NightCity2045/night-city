// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Housing.Components;

/// <summary>
/// Dynamic property lock independent from the static SS14 access-level prototype list.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCPropertyLockComponent : Component
{
    [DataField(required: true, customTypeSerializer: typeof(NCApartmentIdSerializer))]
    [AutoNetworkedField]
    public NCApartmentId PropertyId;

    [DataField(required: true)]
    [AutoNetworkedField]
    public NCPropertyPermission RequiredPermission = NCPropertyPermission.Enter;

    [DataField]
    [AutoNetworkedField]
    public int AccessRevision;

    [DataField]
    [AutoNetworkedField]
    public bool Enabled = true;
}
