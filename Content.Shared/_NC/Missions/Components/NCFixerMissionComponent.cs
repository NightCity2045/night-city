// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Missions.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Components;

/// <summary>Mission service on a physical contact. The server validates proximity and the configured body prototype.</summary>
[RegisterComponent]
public sealed partial class NCFixerMissionComponent : Component
{
    [DataField(required: true)] public ProtoId<NCFixerContactPrototype> Contact;
}

/// <summary>Legacy mapper anchor that places the configured physical mission contact.</summary>
[RegisterComponent]
public sealed partial class NCFixerContactAnchorComponent : Component
{
    [DataField(required: true)] public ProtoId<NCFixerContactPrototype> Contact;
    [DataField] public float SpawnRadius = 2f;
}

/// <summary>Only items spawned by a specific mission run can satisfy its delivery objective.</summary>
[RegisterComponent]
public sealed partial class NCFixerMissionObjectiveComponent : Component;
