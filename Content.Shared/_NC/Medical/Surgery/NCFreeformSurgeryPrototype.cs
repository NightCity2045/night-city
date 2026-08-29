// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using System.Numerics;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

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

    /// <summary>
    /// Physical time required to remove a tool which has been left attached to an operative site.
    /// </summary>
    [DataField]
    public TimeSpan AttachmentRemovalDuration = TimeSpan.FromSeconds(1.5);

    [DataField]
    public int MaximumStrokePoints = 32;

    /// <summary>
    /// NC: Retained for prototype and downstream API compatibility. Incisions are no longer evicted when this
    /// count is exceeded; overlapping strokes are merged spatially instead.
    /// </summary>
    [DataField]
    public int MaximumPersistentIncisions = 64;

    [DataField]
    public float MaximumStrokeLength = 4f;

    /// <summary>
    /// NC: Minimum normalized edge separation that exposes the tissue beneath an incision.
    /// Shared by authority checks and both surgical views so presentation cannot disagree with gameplay.
    /// </summary>
    [DataField]
    public float MinimumOpenRetraction = 0.2f;

    [DataField]
    public float MinimumStrokeLength = 0.015f;

    /// <summary>
    /// Normalized pick radius used to associate a point-tool click with an existing incision.
    /// </summary>
    [DataField]
    public float IncisionSelectionRadius = 0.11f;

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

    /// <summary>
    /// NC: Client-safe sprite atlases used by both the body overview and the magnified operative site.
    /// Gameplay never derives anatomy from these textures; server-owned entities remain authoritative.
    /// </summary>
    [DataField(required: true)]
    public ResPath AnatomyAtlas;

    [DataField]
    public Vector2 AnatomyAtlasGrid = new(5f, 4f);

    /// <summary>
    /// NC: Co-registered full-body skin, skeleton, organ and radiographic projections. Every cell uses the same
    /// authored body coordinates so an operative aperture can reveal anatomy without stretching or drifting.
    /// </summary>
    [DataField(required: true)]
    public ResPath BodyLayerAtlas;

    [DataField]
    public Vector2 BodyLayerAtlasGrid = new(2f, 2f);

    [DataField]
    public Vector2 BodySkinSprite;

    [DataField]
    public Vector2 BodySkeletonSprite = new(1f, 0f);

    [DataField]
    public Vector2 BodyOrganSprite = new(0f, 1f);

    [DataField]
    public Vector2 BodyRadiographSprite = new(1f, 1f);

    [DataField(required: true)]
    public ResPath WoundAtlas;

    [DataField]
    public Vector2 WoundAtlasGrid = new(4f, 3f);

    [DataField]
    public Dictionary<NCTissueLayerKind, Vector2> LayerSprites = new();

    [DataField]
    public Dictionary<NCFreeformSurgeryStructureVisual, Vector2> StructureSprites = new();

    [DataField]
    public Dictionary<NCWoundType, Vector2> WoundSprites = new();

    /// <summary>
    /// Radius of the external ballistic trace in authored body-canvas pixels. Retained projectiles use a filled
    /// marker while extracted projectiles leave a hollow marker at the immutable impact coordinate.
    /// </summary>
    [DataField]
    public float BallisticMarkerRadius = 1.8f;

    [DataField]
    public Vector2 IncisionSprite = new(1f, 1f);

    [DataField]
    public Vector2 ClosedSiteSprite;

    [DataField]
    public Vector2 RetractedSiteSprite = new(1f, 0f);

    [DataField]
    public Vector2 SutureSprite = new(3f, 1f);

    [DataField]
    public Vector2 ScarSprite = new(3f, 2f);

    /// <summary>
    /// Standalone pixel sprite used only after an embedded projectile is physically exposed.
    /// </summary>
    [DataField(required: true)]
    public ResPath ForeignBodySprite;

    /// <summary>
    /// Pixel-perfect closed-skin silhouette used by the interactive surgical field. Its alpha channel is the
    /// outer hit mask; body-zone rectangles only resolve which authoritative region owns an opaque pixel.
    /// </summary>
    [DataField(required: true)]
    public ResPath BodySilhouette;

    [DataField]
    public Vector2 BodyCanvasSize = new(128f, 192f);

    /// <summary>
    /// Client presentation for each anatomical zone on the surgical silhouette. Tool effects remain server-
    /// authoritative; these values only map a clicked pixel into the zone's normalized surgical coordinates.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<NCTargetZonePrototype>, NCFreeformSurgeryBodyZoneDefinition> BodyZones = new();
}

/// <summary>
/// Data-driven projection of one medical zone onto the schematic surgery body.
/// </summary>
[DataDefinition]
public sealed partial class NCFreeformSurgeryBodyZoneDefinition
{
    [DataField(required: true)]
    public Vector2 Position;

    [DataField(required: true)]
    public Vector2 Size;

    /// <summary>
    /// Size, in silhouette pixels, of the local retracted operative aperture.
    /// </summary>
    [DataField(required: true)]
    public Vector2 OpeningSize;

    /// <summary>
    /// Canonical centre of the zone's discrete operative site. Skin incision, retraction and closure clicks
    /// resolve here regardless of the exact opaque silhouette pixel selected by the player.
    /// </summary>
    [DataField]
    public Vector2 InteractionAnchor = new(0.5f, 0.5f);

    /// <summary>
    /// Canonical click-incision axis in normalized zone coordinates.
    /// </summary>
    [DataField]
    public Vector2 IncisionDirection = Vector2.UnitY;

    [DataField]
    public float IncisionLength = 0.28f;
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

    /// <summary>
    /// External surgical zones in which this internal structure may be disclosed and hit-tested. This prevents
    /// broad organ-category ancestry (for example Torso) from leaking skull, pelvis or abdominal organs into
    /// a neighbouring operative window.
    /// </summary>
    [DataField]
    public List<ProtoId<NCTargetZonePrototype>> Zones = [];

    /// <summary>
    /// When closed, this structure occludes deeper structures under the same cursor point. Intended for real
    /// anatomical casings such as the skull and rib cage, not every long bone in a limb.
    /// </summary>
    [DataField]
    public bool BlocksDeeperStructures;

    /// <summary>
    /// Allows the organ-separation capability to detach this exact authored structure. Bone, nerves and
    /// regional containers stay interactive without accidentally becoming removable organs.
    /// </summary>
    [DataField]
    public bool DetachableBySeparation;
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

    /// <summary>
    /// Physical execution time for every interactive use. Completion is always server-revalidated.
    /// </summary>
    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Physical damage applied to an exposed structure when the selected operation cannot use this dangerous
    /// tool as intended. Zero keeps non-destructive instruments on the normal no-target path.
    /// </summary>
    [DataField]
    public float MisuseStructureDamagePerPower;

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
