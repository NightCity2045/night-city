// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Function;

/// <summary>
/// Sanitized whole-body capabilities used by prediction and ordinary gameplay systems.
/// Exact organs, injuries and treatment data remain server-only.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(SharedNCBodyFunctionSystem))]
public sealed partial class NCBodyFunctionComponent : Component
{
    [DataField(required: true), AutoNetworkedField]
    public ProtoId<NCBodyFunctionProfilePrototype> Profile;

    [DataField, AutoNetworkedField]
    public float Movement = 1f;

    [DataField, AutoNetworkedField]
    public float LeftGrip = 1f;

    [DataField, AutoNetworkedField]
    public float RightGrip = 1f;

    [DataField, AutoNetworkedField]
    public float Vision = 1f;

    [DataField, AutoNetworkedField]
    public float Hearing = 1f;

    [DataField, AutoNetworkedField]
    public float Speech = 1f;

    /// <summary>
    /// Monotonic sanitized revision; it carries no medical identifiers.
    /// </summary>
    [DataField, AutoNetworkedField]
    public uint Revision;
}

public readonly record struct NCBodyFunctionAggregate(
    float Movement,
    float LeftGrip,
    float RightGrip,
    float Vision,
    float Hearing,
    float Speech);

[ByRefEvent]
public readonly record struct NCBodyFunctionChangedEvent(
    NCBodyFunctionAggregate Previous,
    NCBodyFunctionAggregate Current);
