// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

[ByRefEvent]
public readonly record struct NCCyberwareInstalledEvent(
    NCActionResolutionId ResolutionId,
    EntityUid Module,
    EntityUid HostOrgan,
    ProtoId<NCCyberwareSlotPrototype> Slot);

[ByRefEvent]
public readonly record struct NCCyberwareRemovedEvent(
    NCActionResolutionId ResolutionId,
    EntityUid Module,
    EntityUid HostOrgan,
    ProtoId<NCCyberwareSlotPrototype> Slot);

[ByRefEvent]
public readonly record struct NCCyberwareStateChangedEvent(
    NCActionResolutionId ResolutionId,
    EntityUid Module,
    NCCyberwareStateChangeKind Kind,
    float PreviousFunction,
    float CurrentFunction);

public enum NCCyberwareStateChangeKind : byte
{
    Installed,
    Removed,
    Power,
    Heat,
    Emp,
    NeuralInterface,
    MechanicalTrauma,
    MechanicalService,
}
