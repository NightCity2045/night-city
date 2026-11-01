// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

[Prototype("ncCyberwarePartSlot")]
public sealed partial class NCCyberwarePartSlotPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
}

[Prototype("ncCyberwarePart")]
public sealed partial class NCCyberwarePartPrototype : IPrototype
{
    [DataField] public List<ProtoId<Content.Shared._NC.Medical.Humanity.NCHumanityPackagePrototype>> HumanityPackages { get; private set; } = [];
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCyberwarePartSlotPrototype> Slot { get; private set; }
    [DataField(required: true)] public float MaximumIntegrity { get; private set; }
    [DataField] public float PermanentDamageThresholdFraction { get; private set; } = 0.5f;
    [DataField] public float PermanentDamageScale { get; private set; } = 0.25f;
}

[Prototype("ncCyberwareAssembly")]
public sealed partial class NCCyberwareAssemblyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)]
    public Dictionary<ProtoId<NCCyberwarePartSlotPrototype>, EntProtoId> DefaultParts { get; private set; } = [];
    [DataField] public ProtoId<StackPrototype> RepairMaterial { get; private set; } = "Steel";
    [DataField] public float RepairPerUnit { get; private set; } = 20f;
    [DataField] public float FrameDamageThresholdFraction { get; private set; } = 0.5f;
    [DataField] public float FramePermanentDamageScale { get; private set; } = 0.25f;
    [DataField] public float InternalDamageThresholdFraction { get; private set; } = 0.5f;
    [DataField] public float HeatPermanentDamageScale { get; private set; } = 0.1f;
}

/// <summary>Physical container state. Detailed configuration is disclosed locally by the service bench.</summary>
[RegisterComponent]
public sealed partial class NCCyberwareAssemblyComponent : Component
{
    [DataField] public new bool Initialized;
    [DataField] public uint ConfigurationRevision = 1;
    [DataField] public uint? ValidatedRevision;
    [ViewVariables] public Dictionary<ProtoId<NCCyberwarePartSlotPrototype>, ContainerSlot> Parts = [];
    [ViewVariables] public float FunctionalEfficiency;
    [ViewVariables] public bool Restoring;
}

[RegisterComponent]
public sealed partial class NCCyberwarePartComponent : Component
{
    [DataField(required: true)] public ProtoId<NCCyberwarePartPrototype> Profile;
    [DataField] public Guid StableInstanceId;
    [DataField] public float PermanentDamage;
    [DataField] public new bool Initialized;
}

[RegisterComponent]
public sealed partial class NCCyberwareWorkbenchComponent : Component
{
    public const string ContainerId = "nc_cyberware_workpiece";
    [DataField] public float TestEnergy = 1f;
    [ViewVariables] public ContainerSlot? Workpiece;
}

[DataDefinition]
public sealed partial class NCCyberwareAssemblySnapshot
{
    [DataField] public uint ConfigurationRevision = 1;
    [DataField] public uint? ValidatedRevision;
    [DataField] public List<NCCyberwarePartSnapshot> Parts = [];
}

[DataDefinition]
public sealed partial class NCCyberwarePartSnapshot
{
    [DataField(required: true)] public ProtoId<NCCyberwarePartSlotPrototype> Slot;
    [DataField] public string EntityPrototype = string.Empty;
    [DataField] public Guid StableInstanceId;
    [DataField] public float CurrentIntegrity;
    [DataField] public float PermanentDamage;
}

/// <summary>One nominal denominator for both damaged frames and replaceable parts; no extra health pool.</summary>
public static class NCCyberwareCondition
{
    public static float ServiceCeiling(float nominal, float permanentDamage)
    {
        if (!float.IsFinite(nominal) || nominal <= 0f || !float.IsFinite(permanentDamage))
            return 0f;
        return nominal * (1f - Math.Clamp(permanentDamage, 0f, 1f));
    }

    public static float Efficiency(float integrity, float nominal)
    {
        return float.IsFinite(integrity) && float.IsFinite(nominal) && nominal > 0f
            ? Math.Clamp(integrity / nominal, 0f, 1f)
            : 0f;
    }

    public static float PermanentDamageAfterImpact(
        float previous, float damage, float nominal, float thresholdFraction, float scale)
    {
        if (!float.IsFinite(previous) || !float.IsFinite(damage) || damage < 0f ||
            !float.IsFinite(nominal) || nominal <= 0f || !float.IsFinite(thresholdFraction) ||
            thresholdFraction < 0f || !float.IsFinite(scale) || scale < 0f)
            return 1f;
        return Math.Clamp(previous + MathF.Max(0f, damage / nominal - thresholdFraction) * scale, 0f, 1f);
    }
}
