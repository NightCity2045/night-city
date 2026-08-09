// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;

namespace Content.Server._NC.Housing.Components;

/// <summary>
/// Persistent data marker for content brought into an apartment after its mapper-authored design was deployed.
/// </summary>
[RegisterComponent]
public sealed partial class NCPropertyResidentItemComponent : Component
{
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId PropertyId;
}
