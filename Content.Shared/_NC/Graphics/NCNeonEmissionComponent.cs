// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Numerics;

namespace Content.Shared._NC.Graphics;

/// <summary>
/// Spills a soft coloured glow from a sign, screen or vending machine into the light map, on top of its point light.
/// </summary>
/// <remarks>
/// The glow is a box drawn before the content light blur, which turns it into a soft pool of colour.
/// It casts no shadows, so keep it small enough not to leak through a wall.
/// While the entity has a point light, the glow follows it: it goes dark when the light is off or packed away.
/// </remarks>
[RegisterComponent]
public sealed partial class NCNeonEmissionComponent : Component
{
    /// <summary>
    /// Glow colour. When unset, the entity's point light colour is used.
    /// </summary>
    [DataField]
    public Color? Color;

    /// <summary>
    /// Width and height of the glowing box, in tiles.
    /// </summary>
    [DataField]
    public Vector2 Size = new(1f, 1f);

    /// <summary>
    /// Centre of the glowing box relative to the entity, in tiles. It turns with the entity.
    /// </summary>
    [DataField]
    public Vector2 Offset;

    /// <summary>
    /// How much light the glow adds, from 0 (none) to 1 (the full colour).
    /// </summary>
    [DataField]
    public float Intensity = 0.5f;
}
