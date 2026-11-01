// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>
/// Declares implant bays on one actual organ entity. The runtime container is authoritative and bounded by
/// this host's declared slots.
/// </summary>
[RegisterComponent]
public sealed partial class NCCyberwareSlotHostComponent : Component
{
    public const string ContainerId = "nc_cyberware_modules";

    [DataField(required: true)]
    public List<ProtoId<NCCyberwareSlotPrototype>> Slots = [];

    [ViewVariables]
    public Container? Modules;
}

/// <summary>
/// Mutable physical state of an implant or mechanical anatomical replacement. It is deliberately not
/// networked; capability-filtered diagnostics are the only player-facing detailed representation.
/// </summary>
[RegisterComponent]
public sealed partial class NCCyberwareComponent : Component
{
    [DataField] public NCCyberwareServiceState Service = new();

    /// <summary>Generic world YAML clock, distinct from the medical snapshot's CapturedAt.</summary>
    [DataField] public TimeSpan? PersistenceCapturedAt;

    [DataField(required: true)]
    public ProtoId<NCCyberwarePrototype> Profile;

    [DataField]
    public Guid StableInstanceId;

    [DataField]
    public ProtoId<NCCyberwareSlotPrototype>? InstalledSlot;

    [DataField]
    public bool StateInitialized;

    [DataField]
    public float CurrentPower;

    // CurrentPower is a legacy serialization field only. Runtime power comes from a physical body bus.
    [ViewVariables]
    public bool PowerGranted;

    [DataField]
    public bool ThermalShutdown;

    [DataField]
    public float Heat;

    [DataField]
    public float Load;

    /// <summary>Trusted compute demand composes with the native active mode and is never restored as authority.</summary>
    [ViewVariables]
    public float ComputeLoad;

    /// <summary>Runtime boundary for timed active modes; the body power solver splits at this deadline.</summary>
    [ViewVariables]
    public TimeSpan? ActiveLoadUntil;

    [DataField]
    public TimeSpan EmpDisabledUntil;

    [DataField]
    public float NeuralIntegrity = 1f;

    [DataField]
    public NCCyberwareNeuralState NeuralState;

    [DataField]
    public bool Calibrated;

    [DataField]
    public bool Operational;

    [DataField]
    public float FunctionalEfficiency = 1f;

    /// <summary>Separate psychological restriction; never changes structural damage or life support.</summary>
    [ViewVariables]
    public float CrisisEfficiency = 1f;
    [ViewVariables]
    public bool CrisisBlocksActiveModes;

    [DataField]
    public float PermanentDamage;

    [ViewVariables]
    public TimeSpan LastAdvancedAt;
}

/// <summary>
/// Runtime replay protection for body-scoped installation, removal and EMP actions. It is intentionally
/// excluded from persistence so an old action identifier never becomes authorization after reconnect.
/// </summary>
[RegisterComponent]
public sealed partial class NCCyberwareBodyStateComponent : Component
{
    [DataField]
    public List<NCCyberwareProcessedActionKey> ProcessedActions = [];

    [DataField]
    public int ProcessedActionCapacity = 128;
}

public enum NCCyberwareNeuralState : byte
{
    NotApplicable,
    Disconnected,
    Linked,
    Degraded,
    Disrupted,
}
