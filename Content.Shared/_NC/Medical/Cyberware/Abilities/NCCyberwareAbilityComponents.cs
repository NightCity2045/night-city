// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Actions;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware.Abilities;

/// <summary>Physical hardware owns its cooldown. Runtime bindings and integral tools are not character identity.</summary>
[RegisterComponent]
public sealed partial class NCCyberwareAbilityComponent : Component
{
    [DataField(required: true)] public ProtoId<NCCyberwareAbilityPrototype> Profile;
    [DataField] public float CooldownRemainingSeconds;
    [ViewVariables] public TimeSpan CooldownObservedAt;
    [ViewVariables] public EntityUid? Body;
    [ViewVariables] public EntityUid? Action;
    [ViewVariables] public EntityUid? IntegralItem;
    [ViewVariables] public ContainerSlot? ItemContainer;
    [ViewVariables] public bool Active;
    [ViewVariables] public TimeSpan? ActiveUntil;
    [ViewVariables] public TimeSpan NextStressAt;
    public const string ContainerId = "nc_cyberware_integral_tool";
}

/// <summary>Sanitized movement/control channels for ordinary client prediction; exact hardware remains private.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class NCCyberwareAbilityBodyComponent : Component
{
    [DataField, AutoNetworkedField] public float MotorBonus;
    [DataField, AutoNetworkedField] public float ManualDurationFactor = 1f;
    [DataField, AutoNetworkedField] public float MovingPenaltyReduction;
    [DataField, AutoNetworkedField] public float MaximumWalkSpeed;
    [DataField, AutoNetworkedField] public float MaximumSprintSpeed;
    [ViewVariables] public HashSet<EntityUid> Modules = [];
}

[ByRefEvent]
public sealed partial class NCActivateCyberwareAbilityEvent : InstantActionEvent;
