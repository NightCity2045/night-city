/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

namespace Content.Shared._NC.ZLevels.Core.EntitySystems;

public abstract partial class NCSharedZLevelsSystem
{
    // NC start: synchronized from the replicated rendering CVar during system initialization.
    public static int MaxZLevelsBelowRendering = 4;
    // NC end
    public const float ZLevelOffset = 0.7f;

    public const float ZGravityForce = 9.8f;
    private const float ZVelocityLimit = 20.0f;
    private const int MaxStepsPerFrame = 10;

    // NC-Edit-Start: tile coordinates never reach the exact 1.0 endpoint before entering the next tile.
    private const float StaircaseUpperEdgeHeight = 0.9f;
    // NC-Edit-End

    /// <summary>
    /// The minimum speed required to trigger LandEvent events.
    /// </summary>
    private const float ImpactVelocityLimit = 3.5f;

    /// <summary>
    /// Distance to ground above which an entity is considered airborne (synced to BodyStatus.InAir).
    /// </summary>
    public const float AirborneHeightThreshold = 0.15f;
}
