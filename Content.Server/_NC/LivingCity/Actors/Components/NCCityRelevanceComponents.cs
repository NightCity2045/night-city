// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Server._NC.LivingCity.Actors.Components;

[Flags]
public enum NCCityDematerializationBlockerFlags : ushort
{
    None = 0,
    Combat = 1 << 0,
    Conversation = 1 << 1,
    Detained = 1 << 2,
    PlayerTreatment = 1 << 3,
    PursuitTarget = 1 << 4,
    ImportantPhysicalState = 1 << 5,
    Scripted = 1 << 6,
}

/// <summary>
/// Round-local blockers owned by gameplay systems. Components contain data only; the relevance system owns policy.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityDematerializationBlockerComponent : Component
{
    [ViewVariables]
    public NCCityDematerializationBlockerFlags Flags;
}

/// <summary>
/// Marks a camera, remote observer or scripted scene anchor as player-relevant without making it a player entity.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityRelevanceAnchorComponent : Component
{
    [DataField]
    public bool Enabled = true;
}
