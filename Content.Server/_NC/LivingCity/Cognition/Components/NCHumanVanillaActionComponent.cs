// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;

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
    [ViewVariables(VVAccess.ReadOnly)] public bool OwnsSteering;
    [ViewVariables(VVAccess.ReadOnly)] public bool SuspendedHtn;
    [ViewVariables(VVAccess.ReadOnly)] public bool OwnsCombatBlocker;
    [ViewVariables(VVAccess.ReadOnly)] public NCCityPhysicalInterruptFlags Interrupt;
    [ViewVariables(VVAccess.ReadOnly)] public string InventorySlot = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public string ExpectedSpeech = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public bool SpeechConfirmed;
}
