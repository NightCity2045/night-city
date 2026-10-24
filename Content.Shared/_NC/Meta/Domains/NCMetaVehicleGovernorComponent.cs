// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Meta.Domains;

/// <summary>Native ECU's predicted upper bound on requested engine torque. It can only reduce the physical engine's result.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCMetaVehicleGovernorComponent : Component
{
    [DataField, AutoNetworkedField] public float PowerLimit = 1f;
    [AutoNetworkedField] public TimeSpan TransmissionUntil;
    [AutoNetworkedField] public float UpshiftMultiplier = 1f;
    [AutoNetworkedField] public int MaximumForwardGear;
    [AutoNetworkedField] public int RequestedForwardGear;
    [AutoNetworkedField] public uint TransmissionAuthority;
    [AutoNetworkedField] public TimeSpan AssistanceUntil;
    [AutoNetworkedField] public uint AssistanceAuthority;
    [AutoNetworkedField] public bool AbsDisabled;
    [AutoNetworkedField] public bool StabilityDisabled;
    [AutoNetworkedField] public bool TractionDisabled;
}
