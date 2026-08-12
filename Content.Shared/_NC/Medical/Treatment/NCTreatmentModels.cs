// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Treatment;

public enum NCTreatmentKind : byte
{
    Pressure,
    Bandage,
    Hemostatic,
    Tourniquet,
    PressureRelease,
    TourniquetRelease,
}

public enum NCTreatmentOutcome : byte
{
    Applied,
    AlreadyApplied,
    PatientNotAuthoritative,
    WoundNotFound,
    NoExternalBleeding,
    InvalidTourniquetZone,
    TourniquetAlreadyApplied,
    TourniquetNotApplied,
    UnsafeReleaseRequiresControlled,
    InvalidProfile,
    UnsupportedItem,
    InvalidAction,
    DuplicateAction,
}

/// <summary>
/// Physical resource consumed only after an authoritative treatment succeeds.
/// </summary>
public enum NCTreatmentResourceKind : byte
{
    None,
    Stack,
    Charges,
}

public enum NCTourniquetIschemiaState : byte
{
    None,
    AtRisk,
    Critical,
}

/// <summary>
/// Persistent-safe wound treatment state. It refers to a wound by its stable Guid and deliberately
/// contains no EntityUid, actor or item reference.
/// </summary>
[DataDefinition]
public sealed partial class NCWoundTreatmentRecord
{
    [DataField]
    public Guid WoundId;

    [DataField]
    public NCWoundTreatmentFlags AppliedFlags;

    [DataField]
    public TimeSpan PressureAppliedAt;

    [DataField]
    public TimeSpan BandagedAt;

    [DataField]
    public TimeSpan HemostaticAppliedAt;

    [DataField]
    public float BandageAbsorptionRemaining;

    [DataField]
    public float BandageSterility;

    [DataField]
    public TimeSpan ModifiedAt;
}

/// <summary>
/// Persistent-safe tourniquet state. Covered wounds are audit data only; rate calculation always uses
/// current server-observed bleeding sources so newly created wounds are handled correctly.
/// </summary>
[DataDefinition]
public sealed partial class NCTourniquetRecord
{
    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> RootZone;

    [DataField]
    public List<Guid> CoveredWoundIds = [];

    [DataField]
    public bool Active = true;

    [DataField]
    public TimeSpan AppliedAt;

    [DataField]
    public TimeSpan IschemiaRiskAt;

    [DataField]
    public TimeSpan CriticalIschemiaAt;

    [DataField]
    public TimeSpan RemovedAt;

    [DataField]
    public TimeSpan ReboundEndsAt;

    [DataField]
    public bool ControlledRelease;
}

public readonly record struct NCTreatmentResult(
    NCTreatmentOutcome Outcome,
    Guid? WoundId,
    ProtoId<NCTargetZonePrototype>? Zone,
    float PreviousBleedRate,
    float CurrentBleedRate)
{
    public bool Succeeded => Outcome == NCTreatmentOutcome.Applied;
}

public readonly record struct NCTourniquetStatus(
    bool Active,
    NCTourniquetIschemiaState Ischemia,
    TimeSpan AppliedAt,
    TimeSpan Elapsed,
    bool ControlledReleaseRequired);

/// <summary>
/// Immutable context for one authoritative treatment attempt. Actor and item identities are reduced to
/// prototype IDs before persistence; transient EntityUid values never enter treatment state.
/// </summary>
public readonly record struct NCTreatmentActionContext(
    NCActionResolutionId ResolutionId,
    TimeSpan Timestamp,
    EntProtoId? ActorPrototype = null,
    EntProtoId? ItemPrototype = null,
    ProtoId<NCTreatmentCapabilityPrototype>? Capability = null);

/// <summary>
/// Bounded persistence-safe audit entry for one treatment resolution.
/// </summary>
[DataDefinition]
public sealed partial class NCTreatmentAuditRecord
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField]
    public NCTreatmentKind Kind;

    [DataField]
    public Guid? WoundId;

    [DataField]
    public ProtoId<NCTargetZonePrototype>? Zone;

    [DataField]
    public EntProtoId? ActorPrototype;

    [DataField]
    public EntProtoId? ItemPrototype;

    [DataField]
    public ProtoId<NCTreatmentCapabilityPrototype>? Capability;

    [DataField]
    public NCTreatmentOutcome Outcome;

    [DataField]
    public float PreviousBleedRate;

    [DataField]
    public float CurrentBleedRate;

    [DataField]
    public TimeSpan Timestamp;
}
