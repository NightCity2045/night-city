// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Groups.Components;

/// <summary>
/// Round-local formation assignment consumed by later physical AI. Persistent snapshots never store EntityUid.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityGroupFormationComponent : Component
{
    [ViewVariables]
    public NCGroupId GroupId;

    [ViewVariables]
    public long SourceGroupVersion;

    [ViewVariables]
    public NCActorId LeaderActorId;

    [ViewVariables]
    public EntityUid LeaderEntity;

    [ViewVariables]
    public string Role = string.Empty;

    [ViewVariables]
    public int SlotIndex;

    [ViewVariables]
    public Vector2 DesiredOffset;

    [ViewVariables]
    public NCCityGroupFormationPolicy Policy;

    [ViewVariables]
    public bool IsLeader;
}
