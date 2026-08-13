// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Emergency;

public enum NCEmergencyCareKind : byte
{
    Cpr,
    Oxygen,
    Ventilation,
    Crystalloid,
    BloodTransfusion,
    Defibrillation,
}

/// <summary>
/// Physical resource owned by an emergency-care item. Vanilla medical stacks use their existing stack
/// count, while reusable devices keep their bounded server-side NC charge counter.
/// </summary>
public enum NCEmergencyCareResourceKind : byte
{
    ComponentCharges,
    Stack,
}

public enum NCEmergencyCareOutcome : byte
{
    Applied,
    PatientNotAuthoritative,
    InvalidProfile,
    InvalidAction,
    DuplicateAction,
    UnsupportedItem,
    DeviceDepleted,
    PatientNotDown,
    ChestTraumaContraindicated,
    NotIndicated,
    WrongRhythm,
    IrreversiblePatient,
    VitalOrganDestroyed,
    IncompatibleBlood,
    VolumeAtCapacity,
}

/// <summary>
/// Immutable trusted context for one authoritative emergency action. Only persistence-safe prototype
/// identity enters the patient audit; callers never persist actor or item EntityUid values.
/// </summary>
public readonly record struct NCEmergencyCareActionContext(
    NCActionResolutionId ResolutionId,
    TimeSpan Timestamp,
    EntProtoId? ActorPrototype = null,
    EntProtoId? ItemPrototype = null,
    ProtoId<NCEmergencyCareCapabilityPrototype>? Capability = null);

public readonly record struct NCEmergencyCareResult(
    NCActionResolutionId ResolutionId,
    NCEmergencyCareOutcome Outcome,
    float PreviousValue,
    float CurrentValue,
    int ChargesConsumed)
{
    public bool Succeeded => Outcome == NCEmergencyCareOutcome.Applied;
}

/// <summary>
/// Bounded persistence-safe action audit. Scalar before/after values are interpreted by Kind: seconds
/// for timed support, millilitres for infusion and rhythm enum values for defibrillation.
/// </summary>
[DataDefinition]
public sealed partial class NCEmergencyCareAuditRecord
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField]
    public NCEmergencyCareKind Kind;

    [DataField]
    public NCEmergencyCareOutcome Outcome;

    [DataField]
    public float PreviousValue;

    [DataField]
    public float CurrentValue;

    [DataField]
    public int ChargesConsumed;

    [DataField]
    public EntProtoId? ActorPrototype;

    [DataField]
    public EntProtoId? ItemPrototype;

    [DataField]
    public ProtoId<NCEmergencyCareCapabilityPrototype>? Capability;

    [DataField]
    public TimeSpan Timestamp;
}

/// <summary>
/// Read-only support values consumed by physiology for one evaluation interval.
/// </summary>
public readonly record struct NCEmergencyPhysiologySupport(
    bool CprActive,
    float AssistedPerfusion,
    float BrainInjuryRateMultiplier,
    float RespiratorySupport,
    float CrystalloidVolume);
