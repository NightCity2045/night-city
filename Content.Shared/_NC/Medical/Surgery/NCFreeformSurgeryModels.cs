// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Persistent, server-authored geometry of one real incision. Points are normalized to the selected regional
/// atlas and are never supplied as a freehand client path. The legacy access depth is derived from these records.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgicalIncisionRecord
{
    [DataField]
    public Guid IncisionId;

    [DataField]
    public Guid? WoundId;

    /// <summary>
    /// Additional trauma records produced while the same canonical operative site is deepened or reopened.
    /// The site remains one interactive incision while every physiological wound stays authoritative.
    /// </summary>
    [DataField]
    public List<Guid> LinkedWoundIds = [];

    [DataField]
    public ProtoId<NCTargetZonePrototype> TargetZone;

    [DataField]
    public NCWoundSurface Surface;

    [DataField]
    public List<Vector2> Polyline = [];

    [DataField]
    public float Depth;

    [DataField]
    public float Width;

    [DataField]
    public float EdgeSeparation;

    [DataField]
    public float Contamination;

    [DataField]
    public bool Sutured;

    /// <summary>
    /// True only when the protective bone casing belonging to this exact zone/surface site was opened.
    /// Keeping this on the incision prevents a thoracic saw cut from exposing the skull or another incision.
    /// </summary>
    [DataField]
    public bool BoneCasingOpen;

    [DataField]
    public NCActionResolutionId SourceResolutionId;

    [DataField]
    public TimeSpan CreatedAt;

    [DataField]
    public TimeSpan ModifiedAt;
}

/// <summary>
/// Runtime attachment marker for a physical tool moved from a hand into the patient's surgical container.
/// The referenced entities are deliberately unsaved; persistent site state retains only semantic aftermath.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCFreeformSurgicalAttachmentComponent : Component
{
    [DataField]
    public EntityUid Patient;

    [DataField]
    public EntityUid Region;

    [DataField]
    public Guid IncisionId;

    [DataField]
    public Vector2 Position;

    [DataField]
    public NCFreeformSurgeryAction Action;

    /// <summary>
    /// Runtime effect contributed by this exact physical attachment. It is never persisted.
    /// </summary>
    [DataField]
    public float ContinuousDrainRate;
}

[Serializable, NetSerializable]
public enum NCFreeformSurgeryFailure : byte
{
    None,
    SessionUnavailable,
    PatientUnavailable,
    OutOfRange,
    InvalidRegion,
    InvalidSurface,
    InvalidInput,
    StaleRevision,
    RateLimited,
    ToolUnavailable,
    ToolBroken,
    CapabilityUnavailable,
    ResourceEmpty,
    AttachmentUnavailable,
    SurgicalAccessClosed,
    NoApplicableTarget,
}

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryCapabilitySummary(
    ProtoId<NCSurgeryToolCapabilityPrototype> Capability,
    string Name,
    string RepresentativeTool,
    NCFreeformSurgeryAction Action,
    bool Selected,
    bool Attachable);

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryLayerSummary(
    NCTissueLayerKind Kind,
    float StartDepth,
    float EndDepth,
    bool Exposed);

/// <summary>
/// Discrete visual state of an authoritative operative site. The client uses this only to choose a sprite;
/// medical access continues to be owned by the real incision, tissue and bone components.
/// </summary>
[Serializable, NetSerializable]
public enum NCInteractiveSurgerySiteStage : byte
{
    Incised,
    Retracted,
    StructuralExposed,
    CavityOpen,
    Sutured,
}

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryIncisionSummary(
    Guid IncisionId,
    ProtoId<NCTargetZonePrototype> Zone,
    Vector2[] Polyline,
    float Depth,
    float Width,
    float EdgeSeparation,
    float Contamination,
    bool Sutured,
    bool BoneCasingOpen,
    NCTissueLayerKind VisibleLayer,
    NCInteractiveSurgerySiteStage Stage);

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryWoundSummary(
    ProtoId<NCTargetZonePrototype> Zone,
    Vector2 Position,
    NCWoundType Type,
    NCWoundSeverity Severity,
    NCTissueDepth Depth,
    float ExternalBleedRate,
    float InternalBleedRate,
    bool EmbeddedObject,
    bool ForeignBodyExposed,
    NCWoundClosureState ClosureState);

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryAttachmentSummary(
    int Handle,
    string Name,
    Vector2 Position,
    NCFreeformSurgeryAction Action);

[Serializable, NetSerializable]
public enum NCFreeformSurgeryStructureKind : byte
{
    Region,
    Organ,
    Bone,
    Tendon,
    Nerve,
    Cyberware,
}

[Serializable, NetSerializable]
public enum NCFreeformSurgeryStructureVisual : byte
{
    Region,
    GenericOrgan,
    Skull,
    Brain,
    Eyes,
    Tongue,
    Ears,
    RibCage,
    Lungs,
    Heart,
    Liver,
    Stomach,
    Kidneys,
    Appendix,
    Pelvis,
    LongBone,
    Tendon,
    Nerve,
    Cyberware,
}

/// <summary>
/// Server-authored clinical appearance. The client may tint or overlay a sprite, but never derives health
/// from hidden components or guesses whether a repair succeeded.
/// </summary>
[Serializable, NetSerializable]
public enum NCFreeformSurgeryDamageVisualStage : byte
{
    Healthy,
    Damaged,
    Critical,
    Ruptured,
    Repaired,
    Bruised,
    Cracked,
    Fractured,
    Displaced,
    Shattered,
    Fixed,
}

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryStructureSummary(
    int Handle,
    string Name,
    NCFreeformSurgeryStructureKind Kind,
    NCFreeformSurgeryStructureVisual Visual,
    Vector2 Position,
    Vector2 Size,
    float MinimumDepth,
    float Integrity,
    float Function,
    NCFreeformSurgeryDamageVisualStage DamageVisual,
    bool ProtectiveCasing,
    bool Exposed);

/// <summary>
/// Private server-sanitized view of a live surgical session. The patient's network identity is included only so
/// the operating client can render the real humanoid sprite; no organ, wound or tool EntityUid is disclosed.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryState(
    NCActionResolutionId sessionId,
    uint revision,
    uint lastSequence,
    bool accepted,
    NCFreeformSurgeryFailure failure,
    ProtoId<NCAnatomyProfilePrototype> anatomyProfile,
    NetEntity patient,
    ProtoId<NCTargetZonePrototype> selectedZone,
    Guid? activeIncisionId,
    NCWoundSurface surface,
    string patientName,
    string toolName,
    string toolPrototype,
    float toolSterility,
    NCPhysiologicalCondition condition,
    NCConsciousnessState consciousness,
    float oxygenation,
    float perfusion,
    float pain,
    float shock,
    float movement,
    float selectedDepth,
    float siteContamination,
    NCSurgicalAccessDepth accessDepth,
    NCFreeformSurgeryCapabilitySummary[] capabilities,
    NCFreeformSurgeryLayerSummary[] layers,
    NCFreeformSurgeryStructureSummary[] structures,
    NCFreeformSurgeryIncisionSummary[] incisions,
    NCFreeformSurgeryWoundSummary[] wounds,
    NCFreeformSurgeryAttachmentSummary[] attachments) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public uint Revision { get; } = revision;
    public uint LastSequence { get; } = lastSequence;
    public bool Accepted { get; } = accepted;
    public NCFreeformSurgeryFailure Failure { get; } = failure;
    public ProtoId<NCAnatomyProfilePrototype> AnatomyProfile { get; } = anatomyProfile;
    public NetEntity Patient { get; } = patient;
    public ProtoId<NCTargetZonePrototype> SelectedZone { get; } = selectedZone;
    public Guid? ActiveIncisionId { get; } = activeIncisionId;
    public NCWoundSurface Surface { get; } = surface;
    public string PatientName { get; } = patientName;
    public string ToolName { get; } = toolName;
    public string ToolPrototype { get; } = toolPrototype;
    public float ToolSterility { get; } = toolSterility;
    public NCPhysiologicalCondition Condition { get; } = condition;
    public NCConsciousnessState Consciousness { get; } = consciousness;
    public float Oxygenation { get; } = oxygenation;
    public float Perfusion { get; } = perfusion;
    public float Pain { get; } = pain;
    public float Shock { get; } = shock;
    public float Movement { get; } = movement;
    public float SelectedDepth { get; } = selectedDepth;
    public float SiteContamination { get; } = siteContamination;
    public NCSurgicalAccessDepth AccessDepth { get; } = accessDepth;
    public NCFreeformSurgeryCapabilitySummary[] Capabilities { get; } = capabilities;
    public NCFreeformSurgeryLayerSummary[] Layers { get; } = layers;
    public NCFreeformSurgeryStructureSummary[] Structures { get; } = structures;
    public NCFreeformSurgeryIncisionSummary[] Incisions { get; } = incisions;
    public NCFreeformSurgeryWoundSummary[] Wounds { get; } = wounds;
    public NCFreeformSurgeryAttachmentSummary[] Attachments { get; } = attachments;
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryRefreshRequest(NCActionResolutionId sessionId) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgerySelectionRequest(
    NCActionResolutionId sessionId,
    ProtoId<NCTargetZonePrototype> zone,
    NCWoundSurface surface,
    float depth) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public ProtoId<NCTargetZonePrototype> Zone { get; } = zone;
    public NCWoundSurface Surface { get; } = surface;
    public float Depth { get; } = depth;
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryCapabilityRequest(
    NCActionResolutionId sessionId,
    ProtoId<NCSurgeryToolCapabilityPrototype> capability) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public ProtoId<NCSurgeryToolCapabilityPrototype> Capability { get; } = capability;
}

/// <summary>
/// NC: Opens one real incision in the magnified operative view, or returns to the body overview when the
/// incision id is null. The server resolves the requested zone back to the patient's body before accepting it.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCFreeformSurgerySiteRequest(
    NCActionResolutionId sessionId,
    ProtoId<NCTargetZonePrototype> zone,
    NCWoundSurface surface,
    Guid? incisionId) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public ProtoId<NCTargetZonePrototype> Zone { get; } = zone;
    public NCWoundSurface Surface { get; } = surface;
    public Guid? IncisionId { get; } = incisionId;
}

/// <summary>
/// One Ocular-style interaction with the topmost visible layer under the cursor. The server constructs the
/// canonical incision/contact geometry from the anatomy profile, so clients cannot submit freehand cuts.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCInteractiveSurgeryActionRequest(
    NCActionResolutionId sessionId,
    uint sequence,
    uint expectedRevision,
    ProtoId<NCTargetZonePrototype> zone,
    NCWoundSurface surface,
    float depth,
    Vector2 position) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public uint Sequence { get; } = sequence;
    public uint ExpectedRevision { get; } = expectedRevision;
    public ProtoId<NCTargetZonePrototype> Zone { get; } = zone;
    public NCWoundSurface Surface { get; } = surface;
    public float Depth { get; } = depth;
    public Vector2 Position { get; } = position;
}

/// <summary>
/// One timed interactive surgery click. The payload contains only the original client intent; the server
/// re-resolves the live session, held tool, capability, region, access path and target after the DoAfter ends.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCFreeformSurgeryDoAfterEvent : DoAfterEvent
{
    [DataField] public NCActionResolutionId SessionId;
    [DataField] public uint Sequence;
    [DataField] public uint ExpectedRevision;
    [DataField] public ProtoId<NCTargetZonePrototype> Zone;
    [DataField] public NCWoundSurface Surface;
    [DataField] public float Depth;
    [DataField] public Vector2 Position;
    [DataField] public ProtoId<NCSurgeryToolCapabilityPrototype> Capability;

    private NCFreeformSurgeryDoAfterEvent()
    {
    }

    public NCFreeformSurgeryDoAfterEvent(
        NCInteractiveSurgeryActionRequest request,
        ProtoId<NCSurgeryToolCapabilityPrototype> capability)
    {
        SessionId = request.SessionId;
        Sequence = request.Sequence;
        ExpectedRevision = request.ExpectedRevision;
        Zone = request.Zone;
        Surface = request.Surface;
        Depth = request.Depth;
        Position = request.Position;
        Capability = capability;
    }

    public NCInteractiveSurgeryActionRequest ToRequest()
    {
        return new NCInteractiveSurgeryActionRequest(
            SessionId,
            Sequence,
            ExpectedRevision,
            Zone,
            Surface,
            Depth,
            Position);
    }

    public override DoAfterEvent Clone()
    {
        return new NCFreeformSurgeryDoAfterEvent(ToRequest(), Capability);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCFreeformSurgeryDoAfterEvent surgery &&
               surgery.SessionId == SessionId && surgery.Sequence == Sequence;
    }
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryAttachRequest(
    NCActionResolutionId sessionId,
    uint expectedRevision,
    ProtoId<NCTargetZonePrototype> zone,
    Vector2 position) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public uint ExpectedRevision { get; } = expectedRevision;
    public ProtoId<NCTargetZonePrototype> Zone { get; } = zone;
    public Vector2 Position { get; } = position;
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryDetachRequest(
    NCActionResolutionId sessionId,
    uint expectedRevision,
    int handle) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public uint ExpectedRevision { get; } = expectedRevision;
    public int Handle { get; } = handle;
}

/// <summary>
/// Server-owned timed placement of an attachable surgical tool. The held entity remains the directed event
/// source, which lets normal DoAfter hand, movement and damage cancellation rules apply before attachment.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCFreeformSurgeryAttachDoAfterEvent : DoAfterEvent
{
    [DataField] public NCActionResolutionId SessionId;
    [DataField] public uint ExpectedRevision;
    [DataField] public ProtoId<NCTargetZonePrototype> Zone;
    [DataField] public Vector2 Position;
    [DataField] public ProtoId<NCSurgeryToolCapabilityPrototype> Capability;

    private NCFreeformSurgeryAttachDoAfterEvent()
    {
    }

    public NCFreeformSurgeryAttachDoAfterEvent(
        NCFreeformSurgeryAttachRequest request,
        ProtoId<NCSurgeryToolCapabilityPrototype> capability)
    {
        SessionId = request.SessionId;
        ExpectedRevision = request.ExpectedRevision;
        Zone = request.Zone;
        Position = request.Position;
        Capability = capability;
    }

    public NCFreeformSurgeryAttachRequest ToRequest()
    {
        return new NCFreeformSurgeryAttachRequest(SessionId, ExpectedRevision, Zone, Position);
    }

    public override DoAfterEvent Clone()
    {
        return new NCFreeformSurgeryAttachDoAfterEvent(ToRequest(), Capability);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCFreeformSurgeryAttachDoAfterEvent attach &&
               attach.SessionId == SessionId && attach.Zone == Zone;
    }
}

/// <summary>
/// Timed removal of a real surgical attachment. The attached entity is the directed event source and is
/// re-resolved through the opaque session handle when the timer completes.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCFreeformSurgeryDetachDoAfterEvent : DoAfterEvent
{
    [DataField] public NCActionResolutionId SessionId;
    [DataField] public uint ExpectedRevision;
    [DataField] public int Handle;

    private NCFreeformSurgeryDetachDoAfterEvent()
    {
    }

    public NCFreeformSurgeryDetachDoAfterEvent(NCFreeformSurgeryDetachRequest request)
    {
        SessionId = request.SessionId;
        ExpectedRevision = request.ExpectedRevision;
        Handle = request.Handle;
    }

    public NCFreeformSurgeryDetachRequest ToRequest()
    {
        return new NCFreeformSurgeryDetachRequest(SessionId, ExpectedRevision, Handle);
    }

    public override DoAfterEvent Clone()
    {
        return new NCFreeformSurgeryDetachDoAfterEvent(ToRequest());
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCFreeformSurgeryDetachDoAfterEvent detach &&
               detach.SessionId == SessionId && detach.Handle == Handle;
    }
}

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryCloseRequest(NCActionResolutionId sessionId) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
}
