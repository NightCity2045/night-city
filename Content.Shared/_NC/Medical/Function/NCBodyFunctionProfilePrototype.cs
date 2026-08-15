// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Function;

/// <summary>
/// Data-driven policy for reducing live anatomy to coarse gameplay-safe body functions.
/// Detailed organ state never leaves the server; clients receive only the resulting bounded values.
/// </summary>
[Prototype("ncBodyFunctionProfile")]
public sealed partial class NCBodyFunctionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Categories whose <see cref="NCAnatomicalRegionComponent"/> contributions participate in motor output.
    /// The side is used only for grip; movement accepts all three sides.
    /// </summary>
    [DataField(required: true)]
    public List<NCBodyFunctionRegionBinding> Regions { get; private set; } = [];

    [DataField]
    public float ExpectedMovementContribution = 1f;

    [DataField]
    public float ExpectedLeftGripContribution = 1f;

    [DataField]
    public float ExpectedRightGripContribution = 1f;

    [DataField]
    public float ExpectedVisionCapacity = 1f;

    [DataField]
    public float ExpectedHearingCapacity = 1f;

    [DataField]
    public float ExpectedSpeechCapacity = 1f;

    /// <summary>
    /// Multipliers indexed by <see cref="NCBoneState"/>.
    /// </summary>
    [DataField(required: true)]
    public List<float> BoneStateMultipliers { get; private set; } = [];

    /// <summary>
    /// Motor multipliers indexed by <see cref="Physiology.NCPainLevel"/>.
    /// </summary>
    [DataField(required: true)]
    public List<float> PainMotorMultipliers { get; private set; } = [];

    /// <summary>
    /// Motor multipliers indexed by <see cref="Physiology.NCConsciousnessState"/>.
    /// </summary>
    [DataField(required: true)]
    public List<float> ConsciousnessMotorMultipliers { get; private set; } = [];

    /// <summary>
    /// Vision, hearing and speech multipliers indexed by <see cref="Physiology.NCConsciousnessState"/>.
    /// </summary>
    [DataField(required: true)]
    public List<float> ConsciousnessSensoryMultipliers { get; private set; } = [];

    [DataField]
    public float DislocatedJointMultiplier = 0.2f;

    [DataField]
    public float RupturedTendonMultiplier;

    [DataField]
    public float SprintFunctionThreshold = 0.65f;

    [DataField]
    public float RestrictedSprintMultiplier = 0.4f;

    [DataField]
    public float FunctionalGripThreshold = 0.2f;

    [DataField]
    public float FunctionalVisionThreshold = 0.05f;

    [DataField]
    public float FunctionalHearingThreshold = 0.05f;

    [DataField]
    public float FunctionalSpeechThreshold = 0.05f;
}

/// <summary>
/// Associates one stable organ category with a body side. Contributions remain on the anatomical region itself.
/// </summary>
[DataDefinition]
public sealed partial class NCBodyFunctionRegionBinding
{
    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> Category;

    [DataField]
    public NCBodyFunctionSide Side;

    /// <summary>
    /// Locomotion and manipulation are deliberately separate: an injured arm cannot slow the legs.
    /// </summary>
    [DataField]
    public bool IncludeMovement;

    [DataField]
    public bool IncludeGrip;

    /// <summary>
    /// Makes a distal region depend on the local motor state and support structures of its direct parent limb.
    /// </summary>
    [DataField]
    public bool IncludeParentSupport;
}

public enum NCBodyFunctionSide : byte
{
    Center,
    Left,
    Right,
}
