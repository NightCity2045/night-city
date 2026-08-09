// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._NC.City.ZoneTemplates.Prototypes;

/// <summary>
/// Data-driven multi-floor map package deployable into a compatible persistent city zone.
/// </summary>
[Prototype("ncZoneTemplate")]
public sealed partial class NCZoneTemplatePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; } = default!;

    /// <summary>
    /// Exact apartment or dungeon slot whose geometry this map package must match.
    /// </summary>
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId TargetZoneId { get; private set; }

    [DataField(required: true)]
    public NCZoneTemplateKind Kind { get; private set; }

    [DataField(required: true)]
    public int Revision { get; private set; }

    [DataField(required: true)]
    public HashSet<ProtoId<NCZoneKindPrototype>> CompatibleZoneKinds { get; private set; } = [];

    [DataField(required: true)]
    public List<NCZoneTemplateFloor> Floors { get; private set; } = [];

    [DataField(required: true)]
    public NCZoneTemplateTilePolicy TilePolicy { get; private set; }

    [DataField(required: true)]
    public NCZoneTemplateEntityPolicy EntityPolicy { get; private set; }

    [DataField(required: true)]
    public NCZoneTemplateCleanupPolicy CleanupPolicy { get; private set; }

    [DataField(required: true)]
    public NCZoneTemplateCachePolicy CachePolicy { get; private set; }
}

/// <summary>
/// One source map in a multi-floor template package.
/// </summary>
[DataDefinition]
public sealed partial class NCZoneTemplateFloor
{
    [DataField(required: true)]
    public int RelativeZ { get; private set; }

    [DataField(required: true)]
    public ResPath MapPath { get; private set; }
}

public enum NCZoneTemplateKind : byte
{
    ApartmentInterior,
    DungeonLayout,
}

public enum NCZoneTemplateTilePolicy : byte
{
    Preserve,
    ReplaceNonEmpty,
    ReplaceAllAllowedTiles,
}

public enum NCZoneTemplateEntityPolicy : byte
{
    None,
    InteriorOnly,
    FullLayout,
}

public enum NCZoneTemplateCleanupPolicy : byte
{
    PreserveDeployment,
    DeleteDeploymentEntities,
    RestoreBaseline,
}

public enum NCZoneTemplateCachePolicy : byte
{
    Always,
    Warm,
    OnDemand,
    Never,
}
