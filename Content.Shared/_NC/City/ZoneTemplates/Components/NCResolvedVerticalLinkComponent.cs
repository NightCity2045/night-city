// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;

namespace Content.Shared._NC.City.ZoneTemplates.Components;

/// <summary>
/// Runtime endpoint resolved from semantic template markers. It stores stable Z-network coordinates, not an EntityUid.
/// </summary>
[RegisterComponent]
public sealed partial class NCResolvedVerticalLinkComponent : Component
{
    [DataField(required: true)] public NCMapCoordinates Target;
    [DataField(required: true)] public NCVerticalLinkType LinkType;
}
