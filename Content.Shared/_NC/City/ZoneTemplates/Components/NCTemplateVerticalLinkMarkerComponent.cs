// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.City.ZoneTemplates.Components;

/// <summary>
/// Semantic source marker resolved into a real Z-level connection after every floor is deployed.
/// </summary>
[RegisterComponent]
public sealed partial class NCTemplateVerticalLinkMarkerComponent : Component
{
    [DataField(required: true)]
    public string LinkId = string.Empty;

    [DataField]
    public int RelativeZ;

    [DataField(required: true)]
    public NCVerticalLinkType LinkType;

    [DataField(required: true)]
    public NCVerticalLinkDirection Direction;

    [DataField(required: true)]
    public string TargetLinkId = string.Empty;

    [DataField]
    public bool Required = true;
}

public enum NCVerticalLinkType : byte
{
    Ladder,
    Stairs,
    Elevator,
    Shaft,
    Portal,
}

public enum NCVerticalLinkDirection : byte
{
    Bidirectional,
    SourceToTarget,
    TargetToSource,
}
