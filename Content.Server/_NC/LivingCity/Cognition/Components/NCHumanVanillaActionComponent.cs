// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Map;

namespace Content.Server._NC.LivingCity.Cognition.Components;

/// <summary>
/// Round-local ownership for one vanilla action. The component stores no behavior; the adapter owns all logic.
/// </summary>
[RegisterComponent]
public sealed partial class NCHumanVanillaActionComponent : Component
{
    [ViewVariables(VVAccess.ReadOnly)] public Guid InstanceId;
    [ViewVariables(VVAccess.ReadOnly)] public NCHumanActionOperationKind Operation;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid Target = EntityUid.Invalid;
    [ViewVariables(VVAccess.ReadOnly)] public NCMapCoordinates TargetCoordinates;
    [ViewVariables(VVAccess.ReadOnly)] public bool HasTargetCoordinates;
    [ViewVariables(VVAccess.ReadOnly)] public uint PatrolCycle;
    [ViewVariables(VVAccess.ReadOnly)] public bool OwnsSteering;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid? RegisteredSteeringTarget;
    [ViewVariables(VVAccess.ReadOnly)] public MapCoordinates RegisteredSteeringDestination;
    [ViewVariables(VVAccess.ReadOnly)] public bool HasRegisteredSteeringDestination;
    [ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime SteeringRegisteredAt;
    [ViewVariables(VVAccess.ReadOnly)] public bool SuspendedHtn;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalInterruptFlags Interrupt;
    [ViewVariables(VVAccess.ReadOnly)] public string InventorySlot = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public string ExpectedSpeech = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public bool SpeechConfirmed;
}
