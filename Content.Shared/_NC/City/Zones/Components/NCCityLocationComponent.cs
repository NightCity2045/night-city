// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.City.Zones.Components;

/// <summary>
/// Networked data-only cache for entities whose current semantic city location is needed repeatedly.
/// Logic and mutations belong to the server NCCityLocationSystem.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCCityLocationComponent : Component
{
    [DataField, AutoNetworkedField]
    public NCCityLocationContext Context;
}
