// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;
using Robust.Shared.Utility;

namespace Content.Shared._NC.Vehicle.Workshop;

/// <summary>Authored workshop geometry; normalized coordinates are identical on client and authority.</summary>
[Prototype("ncVehicleWorkshop")]
public sealed partial class NCVehicleWorkshopPrototype : IPrototype, IInheritingPrototype
{
    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<NCVehicleWorkshopPrototype>))]
    public string[]? Parents { get; private set; }
    [AbstractDataField, NeverPushInheritance]
    public bool Abstract { get; private set; }
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public Dictionary<string, NCVehicleWorkshopView> Views = new();
    [DataField(required: true)] public Dictionary<string, NCVehicleWorkshopNode> Nodes = new();
    [DataField(required: true)] public ResPath FastenerTexture;
    [DataField(required: true)] public ResPath ConnectorTexture;
    [DataField] public float PointSeconds = 1.5f;
    [DataField] public float TransferSeconds = 3;
    [DataField] public float InspectSeconds = 2;
    [DataField] public float RefreshSeconds = 0.5f;
    /// <summary>Per-view body proportions and physical access points for a specific chassis.</summary>
    [DataField] public Dictionary<string, NCVehicleWorkshopViewLayout> ViewLayouts = new();
    /// <summary>Small model-specific corrections applied after the containing view layout.</summary>
    [DataField] public Dictionary<string, NCVehicleWorkshopNodeLayout> NodeLayouts = new();
}

[DataDefinition]
public sealed partial class NCVehicleWorkshopViewLayout
{
    [DataField] public Vector2 Scale = Vector2.One;
    [DataField] public Vector2 Offset;
    [DataField] public Vector2? AccessPosition;
    [DataField] public float? AccessRange;
}

[DataDefinition]
public sealed partial class NCVehicleWorkshopNodeLayout
{
    [DataField] public Vector2 Scale = Vector2.One;
    [DataField] public Vector2 Offset;
}

[DataDefinition]
public sealed partial class NCVehicleWorkshopView
{
    [DataField(required: true)] public LocId Name;
    [DataField] public bool RequiresLift;
    [DataField] public bool RequiresHood;
    [DataField] public Vector2 AccessPosition;
    [DataField] public float AccessRange = 2;
}

[DataDefinition]
public sealed partial class NCVehicleWorkshopNode
{
    [DataField(required: true)] public string Slot = string.Empty;
    [DataField(required: true)] public string View = string.Empty;
    [DataField(required: true)] public ResPath Texture;
    [DataField(required: true)] public Vector2 Position;
    [DataField(required: true)] public Vector2 Size;
    [DataField] public int Layer;
    // Elliptic picking keeps empty corners of circular parts from swallowing clicks.
    [DataField] public bool Ellipse;
    /// <summary>Shown only when this carrier is loaded into a component machine.</summary>
    [DataField] public bool DetachedOnly;
    /// <summary>Optional service port drawn over this physical assembly.</summary>
    [DataField] public NCVehicleFluidCircuit? Fluid;
    /// <summary>Coarse alpha mask exported with the art; authority needs no GPU or texture decoder.</summary>
    [DataField] public string[] HitMask = [];
}

[Serializable, NetSerializable]
public enum NCVehicleWorkshopPointKind : byte { Fastener, Connector }

[DataDefinition]
public sealed partial class NCVehicleWorkshopPointDefinition
{
    [DataField(required: true)] public Vector2 Position;
    [DataField] public float Radius = 0.045f;
    [DataField] public NCVehicleWorkshopPointKind Kind;
    [DataField] public string ToolQuality = "Anchoring";
    [DataField] public LocId Name = "nc-workshop-fastener";
    /// <summary>Required tightening torque in Nm. Zero keeps binary fastener behavior.</summary>
    [DataField] public float TargetTorque;
    [DataField] public float TorqueTolerance = 0.1f;
}
