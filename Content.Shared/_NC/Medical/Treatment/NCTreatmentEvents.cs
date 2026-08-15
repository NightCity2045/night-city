// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Treatment;

/// <summary>
/// Server-local audit/integration event emitted after an authoritative first-aid mutation.
/// </summary>
[ByRefEvent]
public readonly record struct NCTreatmentChangedEvent(
    NCActionResolutionId ResolutionId,
    NCTreatmentKind Kind,
    Guid? WoundId,
    ProtoId<NCTargetZonePrototype>? Zone,
    float PreviousBleedRate,
    float CurrentBleedRate);

/// <summary>
/// Immutable treatment intent carried through the cancellable engine DoAfter.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCTreatmentDoAfterEvent : DoAfterEvent
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField]
    public Guid WoundId;

    [DataField]
    public ProtoId<NCTreatmentCapabilityPrototype> Capability;

    private NCTreatmentDoAfterEvent()
    {
    }

    public NCTreatmentDoAfterEvent(
        NCActionResolutionId resolutionId,
        Guid woundId,
        ProtoId<NCTreatmentCapabilityPrototype> capability)
    {
        ResolutionId = resolutionId;
        WoundId = woundId;
        Capability = capability;
    }

    public override DoAfterEvent Clone()
    {
        return new NCTreatmentDoAfterEvent(ResolutionId, WoundId, Capability);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCTreatmentDoAfterEvent treatment && treatment.ResolutionId == ResolutionId;
    }
}
