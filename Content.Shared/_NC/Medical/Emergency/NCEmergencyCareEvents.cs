// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Emergency;

/// <summary>
/// Server-local integration event emitted after an emergency action mutates patient or device state.
/// </summary>
[ByRefEvent]
public readonly record struct NCEmergencyCareChangedEvent(
    NCActionResolutionId ResolutionId,
    NCEmergencyCareKind Kind,
    NCEmergencyCareOutcome Outcome);

/// <summary>
/// Server-authored item intervention carried through a cancellable DoAfter.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCEmergencyCareDoAfterEvent : DoAfterEvent
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField]
    public ProtoId<NCEmergencyCareCapabilityPrototype> Capability;

    private NCEmergencyCareDoAfterEvent()
    {
    }

    public NCEmergencyCareDoAfterEvent(
        NCActionResolutionId resolutionId,
        ProtoId<NCEmergencyCareCapabilityPrototype> capability)
    {
        ResolutionId = resolutionId;
        Capability = capability;
    }

    public override DoAfterEvent Clone()
    {
        return new NCEmergencyCareDoAfterEvent(ResolutionId, Capability);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCEmergencyCareDoAfterEvent care && care.ResolutionId == ResolutionId;
    }
}

/// <summary>
/// Empty-hand CPR intent. The action identifier gives repeated delivery the same medical resolution.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCCprDoAfterEvent : DoAfterEvent
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    private NCCprDoAfterEvent()
    {
    }

    public NCCprDoAfterEvent(NCActionResolutionId resolutionId)
    {
        ResolutionId = resolutionId;
    }

    public override DoAfterEvent Clone()
    {
        return new NCCprDoAfterEvent(ResolutionId);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCCprDoAfterEvent cpr && cpr.ResolutionId == ResolutionId;
    }
}
