// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Missions.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Components;

/// <summary>Service on a real persistent actor. Binding and physical interaction are checked by the server.</summary>
[RegisterComponent]
public sealed partial class NCFixerMissionComponent : Component
{
    [DataField(required: true)] public ProtoId<NCFixerContactPrototype> Contact;
}

/// <summary>Mapper anchor for the existing physical actor lifecycle; never a substitute NPC body.</summary>
[RegisterComponent]
public sealed partial class NCFixerContactAnchorComponent : Component
{
    [DataField(required: true)] public ProtoId<NCFixerContactPrototype> Contact;
    [DataField] public float SpawnRadius = 2f;
}

/// <summary>Only items spawned by a specific mission run can satisfy its delivery objective.</summary>
[RegisterComponent]
public sealed partial class NCFixerMissionObjectiveComponent : Component;
