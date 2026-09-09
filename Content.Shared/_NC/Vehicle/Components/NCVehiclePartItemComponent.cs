// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCVehiclePartItemComponent : Component
{
    [DataField(required: true), AutoNetworkedField] public ProtoId<NCVehiclePartPrototype> Part;
    [DataField, AutoNetworkedField] public float Condition = 1;
    /// <summary>Disconnected batteries carry their actual remaining amp-hours into the next installation.</summary>
    [DataField, AutoNetworkedField] public float? StoredCharge;
}
