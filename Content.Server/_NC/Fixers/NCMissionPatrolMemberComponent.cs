// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Fixers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Fixers;

/// <summary>Someone walking a street patrol (<see cref="NCMissionPatrolPrototype"/>).</summary>
[RegisterComponent]
public sealed partial class NCMissionPatrolMemberComponent : Component
{
    [ViewVariables]
    public ProtoId<NCMissionPatrolPrototype> Patrol;

    [ViewVariables]
    public int Group;
}
