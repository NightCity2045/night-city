// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.Humanoid;
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

    /// <summary>
    /// Humanoid sprite layers whose alpha defines the visible world silhouette for this anatomical zone.
    /// The wound renderer uses the current RSI direction, so side-facing and prone bodies remain clipped correctly.
    /// </summary>
    [DataField(required: true)]
    public List<HumanoidVisualLayers> WorldVisualLayers { get; private set; } = [];

    /// <summary>
    /// Optional visual size on the 32px doll canvas. Cropped selection textures come from a different
    /// source silhouette than the Status RSI, so limbs use this to match the authoritative status layer.
    /// </summary>
    [DataField]
    public Vector2? DisplaySize { get; private set; }

    /// <summary>
    /// Transparent interactive padding around the visual. This keeps small hands and feet comfortable to
    /// click without changing or displacing the rendered overlay.
    /// </summary>
    [DataField]
    public Vector2 HitboxPadding { get; private set; } = Vector2.Zero;

    /// <summary>
    /// Optional interactive rectangle relative to <see cref="Position"/>. It is independent from the rendered
    /// hover texture, so a broad highlight can never steal clicks from a neighbouring anatomical zone.
    /// </summary>
    [DataField]
    public NCTextureRegion? InteractionRegion { get; private set; }

    /// <summary>
    /// Optional wound-projection rectangle relative to <see cref="Position"/>. This follows the composed Status
    /// silhouette instead of the loose selection texture when those assets have different crops.
    /// </summary>
    [DataField]
    public NCTextureRegion? WoundRegion { get; private set; }

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
