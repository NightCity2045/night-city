// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Client-facing layout for a target-zone catalogue. It contains no anatomy or damage rules.
/// </summary>
[Prototype("ncTargetDoll")]
public sealed partial class NCTargetDollProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public Vector2 CanvasSize { get; private set; } = new(32f, 32f);

    [DataField(required: true)]
    public List<NCTargetDollStatusLayer> StatusLayers { get; private set; } = [];

    [DataField(required: true)]
    public List<NCTargetDollZonePresentation> Zones { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCTargetDollStatusLayer
{
    [DataField(required: true)]
    public string Id { get; private set; } = default!;

    [DataField(required: true)]
    public ResPath Sprite { get; private set; } = default!;

    [DataField(required: true)]
    public string StatePrefix { get; private set; } = default!;

    /// <summary>
    /// Medical zones whose worst sanitized severity colors this visual layer.
    /// Chest and abdomen share the supplied torso status sprite.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<NCTargetZonePrototype>> SourceZones { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCTargetDollZonePresentation
{
    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> Zone { get; private set; }

    [DataField(required: true)]
    public Vector2 Position { get; private set; }

    [DataField(required: true)]
    public ResPath SelectedTexture { get; private set; } = default!;

    [DataField(required: true)]
    public ResPath HoverTexture { get; private set; } = default!;

    [DataField]
    public NCTextureRegion? SelectedRegion { get; private set; }

    [DataField]
    public NCTextureRegion? HoverRegion { get; private set; }
}

[DataDefinition]
public sealed partial class NCTextureRegion
{
    [DataField(required: true)]
    public Vector2 Position { get; private set; }

    [DataField(required: true)]
    public Vector2 Size { get; private set; }
}
