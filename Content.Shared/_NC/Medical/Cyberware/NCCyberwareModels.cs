// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

public readonly record struct NCCyberwareInstallRequest(
    NCActionResolutionId ResolutionId,
    EntityUid Patient,
    EntityUid HostOrgan,
    EntityUid Module,
    ProtoId<NCCyberwareSlotPrototype> Slot,
    bool ConnectNeuralInterface = false,
    bool CalibrateNeuralInterface = false);

public readonly record struct NCCyberwareRemoveRequest(
    NCActionResolutionId ResolutionId,
    EntityUid Patient,
    EntityUid Module);

public readonly record struct NCCyberwareEmpRequest(
    NCActionResolutionId ResolutionId,
    EntityUid Patient,
    float Energy,
    TimeSpan Duration,
    uint Sequence = 0);

public readonly record struct NCCyberwareProcessedActionKey(
    Guid ResolutionId,
    NCCyberwareActionKind Kind,
    uint Sequence,
    uint ImpactSequence);

public enum NCCyberwareActionKind : byte
{
    Install,
    Remove,
    Emp,
    NeuralInterface,
    Service,
    MechanicalTrauma,
    Calibration,
}

public readonly record struct NCCyberwareMutationResult(
    NCCyberwareMutationStatus Status,
    NCCyberwareMutationFailure Failure,
    int AffectedModules = 0)
{
    public bool Accepted => Status is NCCyberwareMutationStatus.Applied or NCCyberwareMutationStatus.Duplicate;
}

public enum NCCyberwareMutationStatus : byte
{
    Rejected,
    Applied,
    Duplicate,
}

public enum NCCyberwareMutationFailure : byte
{
    None,
    InvalidResolution,
    EntityUnavailable,
    PatientNotAuthoritative,
    ModuleUnavailable,
    ProfileUnavailable,
    HostNotInPatient,
    HostSlotUnavailable,
    SlotIncompatible,
    SlotOccupied,
    CapacityExceeded,
    AlreadyInstalled,
    NotInstalledInPatient,
    NeuralInterfaceUnavailable,
    RepairProfileMismatch,
    InvalidAmount,
    PhysicalContainerFailure,
}

/// <summary>
/// Semantic cyberware state used by medical persistence. Host/category keys and prototype IDs replace every
/// runtime entity reference.
/// </summary>
[DataDefinition]
public sealed partial class NCCyberwareStateSnapshot
{
    [DataField] public NCCyberwareServiceState? Service;

    [DataField]
    public string EntityPrototype = string.Empty;

    [DataField(required: true)]
    public ProtoId<NCCyberwarePrototype> Profile;

    [DataField]
    public Guid StableInstanceId;

    [DataField]
    public ProtoId<NCCyberwareSlotPrototype>? Slot;

    [DataField]
    public ProtoId<OrganCategoryPrototype>? HostCategory;

    [DataField]
    public float CurrentIntegrity;

    [DataField]
    public float MaximumIntegrity;

    [DataField]
    public float CurrentPower;

    [DataField]
    public float Heat;

    [DataField]
    public float Load;

    [DataField]
    public TimeSpan EmpDisabledUntil;

    [DataField]
    public float NeuralIntegrity;

    [DataField]
    public NCCyberwareNeuralState NeuralState;

    [DataField]
    public bool Calibrated;

    [DataField]
    public float PermanentDamage;

    [DataField]
    public NCCyberwareAssemblySnapshot? Assembly;

    [DataField]
    public NCCyberwarePowerBusSnapshot? PowerBus;

    [DataField]
    public bool ThermalShutdown;

    [DataField]
    public float? AbilityCooldownSeconds;

    [DataField]
    public Content.Shared._NC.Medical.Cyberware.Visuals.NCCyberwareExteriorSnapshot? Exterior;
}
