// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Persistent, bounded geometry of one real incision. Points are normalized to the selected regional atlas.
/// The legacy access depth is derived from these records and is not the spatial source of truth.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgicalIncisionRecord
{
    [DataField]
    public Guid IncisionId;

    [DataField]
    public Guid? WoundId;

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

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryIncisionSummary(
    Guid IncisionId,
    ProtoId<NCTargetZonePrototype> Zone,
    Vector2[] Polyline,
    float Depth,
    float Width,
    float EdgeSeparation,
    float Contamination,
    bool Sutured);

[Serializable, NetSerializable]
public readonly record struct NCFreeformSurgeryWoundSummary(
    ProtoId<NCTargetZonePrototype> Zone,
    Vector2 Position,
    NCWoundType Type,
    NCWoundSeverity Severity,
    NCTissueDepth Depth,
    float ExternalBleedRate,
    float InternalBleedRate,
    bool EmbeddedObject);

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

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryStrokeRequest(
    NCActionResolutionId sessionId,
    uint sequence,
    uint expectedRevision,
    ProtoId<NCTargetZonePrototype> zone,
    NCWoundSurface surface,
    float depth,
    Vector2[] points) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
    public uint Sequence { get; } = sequence;
    public uint ExpectedRevision { get; } = expectedRevision;
    public ProtoId<NCTargetZonePrototype> Zone { get; } = zone;
    public NCWoundSurface Surface { get; } = surface;
    public float Depth { get; } = depth;
    public Vector2[] Points { get; } = points;
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

[Serializable, NetSerializable]
public sealed class NCFreeformSurgeryCloseRequest(NCActionResolutionId sessionId) : EntityEventArgs
{
    public NCActionResolutionId SessionId { get; } = sessionId;
}
