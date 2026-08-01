// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Zones;

/// <summary>
/// Immutable public view of zone metadata. Runtime systems never expose mutable prototype data or components.
/// </summary>
public readonly record struct NCZoneInfo(
    NCZoneId Id,
    ProtoId<NCZoneKindPrototype> Kind,
    NCZoneId Parent,
    string Name,
    int Priority,
    int HierarchyRank,
    NCZoneContextSlot ContextSlot,
    NCZoneActivityMode ActivityMode);

public readonly record struct NCZoneValidationError(string ZoneSet, NCZoneId Zone, string Message);
