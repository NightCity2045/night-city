// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Housing.Components;

/// <summary>
/// Bearer credential for one apartment. A revision bump invalidates every previously issued physical card.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCPropertyKeyCardComponent : Component
{
    [DataField(required: true)]
    [AutoNetworkedField]
    public Guid GrantId;

    [DataField(required: true, customTypeSerializer: typeof(NCApartmentIdSerializer))]
    [AutoNetworkedField]
    public NCApartmentId PropertyId;

    [DataField(required: true)]
    [AutoNetworkedField]
    public int AccessRevision;

    [DataField(required: true)]
    [AutoNetworkedField]
    public NCPropertyPermission Permissions;

    [DataField]
    [AutoNetworkedField]
    public NCActiveWorldTime? ValidUntil;
}
