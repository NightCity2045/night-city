// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared._NC.Missions.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Components;

/// <summary>
/// Data-only binding from one physical terminal to one public mission definition and stable city slot.
/// </summary>
[RegisterComponent]
public sealed partial class NCMissionConsoleComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCMissionDefinitionPrototype> Definition;

    [DataField(required: true, customTypeSerializer: typeof(NCMissionSlotIdSerializer))]
    public NCMissionSlotId SlotId;
}
