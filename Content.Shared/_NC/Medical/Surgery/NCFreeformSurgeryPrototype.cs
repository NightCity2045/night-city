// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using System.Numerics;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Data-driven limits and physical tool behaviours for one freeform surgical field. The profile contains
/// no character skill or success roll: server-observed tool quality and the submitted geometry determine
/// the result.
/// </summary>
[Prototype("ncFreeformSurgeryProfile")]
public sealed partial class NCFreeformSurgeryProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public float InteractionRange = 2f;

    [DataField]
    public TimeSpan RefreshInterval = TimeSpan.FromSeconds(0.5);

    [DataField]
    public TimeSpan MinimumInputInterval = TimeSpan.FromMilliseconds(40);

    [DataField]
    public int MaximumStrokePoints = 32;

    [DataField]
    public int MaximumPersistentIncisions = 64;

    [DataField]
    public float MaximumStrokeLength = 4f;

    [DataField]
    public float MinimumStrokeLength = 0.015f;

    [DataField]
    public float MovementDisplacement = 0.08f;

    [DataField]
    public float UnpreparedFieldContamination = 0.12f;

    /// <summary>
    /// Integrity thresholds used when a cutting stroke intersects an internal structure. They are profile
    /// data because different anatomies may tolerate incidental surgical trauma differently.
    /// </summary>
    [DataField]
    public float IncidentalTendonRuptureThreshold = 0.45f;

    [DataField]
    public float IncidentalBoneCrackThreshold = 0.75f;

    [DataField]
    public float IncidentalBoneBreakThreshold = 0.4f;

    [DataField(required: true)]
    public Dictionary<ProtoId<NCSurgeryToolCapabilityPrototype>, NCFreeformSurgeryToolDefinition> Tools = new();

    /// <summary>
    /// Spatial atlas positions for real organ categories. Missing categories remain operable through the
    /// region surface but are not disclosed as a guessed internal structure.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, NCFreeformSurgeryStructureDefinition> Structures = new();

    /// <summary>
    /// Atlas placement for installed internal cyberware, keyed by its real host slot. Internal modules live in
    /// slot containers rather than the body's flat organ container, so their geometry is declared separately.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<NCCyberwareSlotPrototype>, NCFreeformSurgeryStructureDefinition> CyberwareSlots = new();
}

[DataDefinition]
public sealed partial class NCFreeformSurgeryStructureDefinition
{
    [DataField(required: true)]
    public Vector2 Position;

    [DataField]
    public Vector2 Size = new(0.2f, 0.2f);

    [DataField]
    public float MinimumDepth = 0.5f;

    /// <summary>
    /// Client-safe pixel silhouette used by the surgical field. Gameplay continues to resolve the real organ.
    /// </summary>
    [DataField]
    public NCFreeformSurgeryStructureVisual Visual = NCFreeformSurgeryStructureVisual.GenericOrgan;
}

/// <summary>
/// One physical use of a tool capability. Numerical values live in YAML so medical balance does not depend
/// on client presentation or hard-coded item names.
/// </summary>
[DataDefinition]
public sealed partial class NCFreeformSurgeryToolDefinition
{
    [DataField(required: true)]
    public NCFreeformSurgeryAction Action;

    [DataField]
    public ProtoId<NCTraumaProfilePrototype>? TraumaProfile;

    [DataField]
    public float MinimumDepth;

    [DataField]
    public float MaximumDepth = 1f;

    [DataField]
    public float PowerPerUnit = 1f;

    [DataField]
    public float MinimumPower;

    [DataField]
    public float MaximumPower = 100f;

    [DataField]
    public float ContaminationPerUnit;

    [DataField]
    public float Radius = 0.12f;

    [DataField]
    public bool Attachable;

    /// <summary>
    /// Requires a real, unsutured incision at the contacted point and requested depth. This is a physical
    /// reachability rule, not a procedure-order gate: any incision or open wound which exposes the point works.
    /// </summary>
    [DataField]
    public bool RequiresOpenAccess;

    [DataField]
    public int ConsumedUses;

    [DataField]
    public float InternalBleedPerPowerAtCavity;

    [DataField]
    public float IncidentalStructureDamagePerPower;

    [DataField]
    public float ImmediateDrainVolumePerPower;

    [DataField]
    public float ContinuousDrainRate;

    /// <summary>
    /// Optional authoritative region-category allowlist for actions which operate on the selected external
    /// region rather than on a structure under the cursor. An empty list leaves the action unrestricted.
    /// </summary>
    [DataField]
    public List<ProtoId<OrganCategoryPrototype>> AllowedRegionCategories = [];
}

public enum NCFreeformSurgeryAction : byte
{
    Disinfect,
    Incise,
    Hemostasis,
    Retract,
    Suture,
    Irrigate,
    Debride,
    RepairVessel,
    RepairOrgan,
    ReduceBone,
    FixBone,
    RepairTendon,
    RepairNerve,
    ExtractForeignBody,
    OpenBone,
    Separate,
    Amputate,
    Transplant,
    Cyberware,
    Cauterize,
    Autopsy,
    DrainCavity,
}
