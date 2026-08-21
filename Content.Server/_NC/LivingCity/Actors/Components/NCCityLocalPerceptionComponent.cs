// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Components;

public enum NCCityLocalObservationKind : byte
{
    Neutral,
    Friendly,
    Hostile,
    Critical,
}

public readonly record struct NCCityLocalObservation(
    EntityUid EntityUid,
    NCCityLocalObservationKind Kind,
    float DistanceSquared);

/// <summary>
/// Bounded, round-local sensory cache. Persistent beliefs and memories consume explicit later events, never this
/// EntityUid-bearing adapter directly.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityLocalPerceptionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public ProtoId<NCCityLocalPerceptionProfilePrototype> Profile;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime LastEvaluatedAt;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime NextEvaluationAt;
    [ViewVariables(VVAccess.ReadOnly)] public long Revision;
    [ViewVariables(VVAccess.ReadOnly)] public int HostileCount;
    [ViewVariables(VVAccess.ReadOnly)] public int CriticalCount;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid NearestHostile = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid NearestCritical = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public List<NCCityLocalObservation> Observations = [];
}
