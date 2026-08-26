// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Cognition;

/// <summary>Names an existing authority. The router never reimplements the gameplay system behind it.</summary>
public enum NCHumanActionExecutorKind : byte
{
    Core,
    VanillaMovement,
    VanillaCombat,
    VanillaInteraction,
    VanillaHands,
    VanillaSpeech,
    ProfessionAdapter,
    MedicalAdapter,
    PoliceAdapter,
    SocialAdapter,
    FixerAdapter,
}

/// <summary>
/// Closed list of vanilla operations. Action prototypes select an operation; the adapter never infers one from prose.
/// </summary>
public enum NCHumanActionOperationKind : byte
{
    None,
    ApproachTarget,
    FollowTarget,
    HoldPosition,
    PatrolZone,
    FleeFromTarget,
    CombatAuthority,
    ActivateTarget,
    AltInteractTarget,
    PickupTarget,
    DropTarget,
    EquipTarget,
    UnequipTarget,
    UseHeldTarget,
    Speak,
    StartPullingTarget,
    StopPulling,
}

public enum NCHumanActionTargetKind : byte
{
    None,
    Person,
    Zone,
    Entity,
    Item,
    DomainRecord,
}

/// <summary>The observable condition that is allowed to finish an action.</summary>
public enum NCHumanActionCompletionKind : byte
{
    PlannerConfirmed,
    ArrivalConfirmed,
    RangeConfirmed,
    InteractionConfirmed,
    WorldStateChanged,
    DomainMutationConfirmed,
    AuthorityReleased,
}

public enum NCHumanActionInterruptKind : byte
{
    PlanChanged,
    Dematerialized,
    Combat,
    Conversation,
    Incapacitated,
    Detained,
    RoundEnded,
    TimedOut,
    ExecutorFailed,
}

/// <summary>
/// Closed data-driven inventory shared by BDI, narrative Actors and the authoritative action router.
/// </summary>
[Prototype("ncHumanAction")]
public sealed partial class NCHumanActionPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public NCHumanActionExecutorKind Executor { get; private set; }
    [DataField(required: true)] public NCHumanActionTargetKind TargetKind { get; private set; }
    [DataField(required: true)] public NCHumanActionCompletionKind Completion { get; private set; }
    [DataField(required: true)] public TimeSpan Timeout { get; private set; }
    [DataField] public NCHumanActionOperationKind Operation { get; private set; }
    [DataField] public bool RequiresPhysicalEntity { get; private set; }
    [DataField] public float MaximumRange { get; private set; }
    [DataField] public float CompletionRange { get; private set; }
    [DataField] public string SpeechLocId { get; private set; } = string.Empty;
    [DataField] public HashSet<NCHumanActionInterruptKind> Interrupts { get; private set; } = [];
}
