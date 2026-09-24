// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.Physics;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.AV.Components;

/// <summary>
/// Marks a shuttle grid as an AV and stores its prototype-tunable flight state.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAVComponent : Component
{
    /// <summary>Maximum vertical speed, measured in Z-levels per second.</summary>
    [DataField, AutoNetworkedField]
    public float MaxVerticalSpeed = 1.5f;

    /// <summary>How quickly vertical velocity approaches the pilot's requested speed.</summary>
    [DataField, AutoNetworkedField]
    public float VerticalResponse = 4f;

    /// <summary>Downward acceleration when the lift fans cannot hold the AV.</summary>
    [DataField, AutoNetworkedField]
    public float Gravity = 9.81f;

    /// <summary>Maximum horizontal speed while airborne, measured in tiles per second.</summary>
    [DataField]
    public float MaxPlanarSpeed = 18f;

    /// <summary>Horizontal acceleration supplied by vectored lift, measured in tiles per second squared.</summary>
    [DataField]
    public float PlanarAcceleration = 7f;

    /// <summary>Automatic horizontal stabilization when the pilot releases movement input.</summary>
    [DataField]
    public float PlanarDeceleration = 5f;

    /// <summary>Maximum rotation speed while airborne, measured in radians per second.</summary>
    [DataField]
    public float MaxAngularSpeed = 1.5f;

    /// <summary>Rotation acceleration supplied by vectored lift, measured in radians per second squared.</summary>
    [DataField]
    public float AngularAcceleration = 3f;

    /// <summary>Static collision layers treated as solid flight obstacles.</summary>
    [DataField]
    public CollisionGroup ObstacleCollisionMask = CollisionGroup.FullTileMask;

    /// <summary>Inset applied to each occupied grid tile during flight collision sweeps.</summary>
    [DataField]
    public float CollisionInset = 0.03f;

    /// <summary>Maximum distance between collision samples, measured in tiles.</summary>
    [DataField]
    public float CollisionSweepStep = 0.25f;

    /// <summary>Maximum world-space displacement of the projected flight shadow.</summary>
    [DataField, AutoNetworkedField]
    public Vector2 FlightShadowOffset = new(0.45f, -0.45f);

    /// <summary>Color of the projected flight shadow.</summary>
    [DataField, AutoNetworkedField]
    public Color FlightShadowColor = Color.FromHex("#02070ACC");

    /// <summary>Color of the lift wash shown beneath the AV.</summary>
    [DataField, AutoNetworkedField]
    public Color FlightGlowColor = Color.FromHex("#2AD9D0");

    /// <summary>Maximum opacity of the lift wash.</summary>
    [DataField, AutoNetworkedField]
    public float FlightGlowAlpha = 0.14f;

    /// <summary>Whether the grid is currently using flight physics.</summary>
    [AutoNetworkedField]
    public bool Airborne;

    /// <summary>Fractional height between the current Z-level and the level above it.</summary>
    [AutoNetworkedField]
    public float Height;

    /// <summary>Current vertical velocity in Z-levels per second.</summary>
    [AutoNetworkedField]
    public float VerticalVelocity;

    /// <summary>Total powered lift currently available to this AV.</summary>
    [AutoNetworkedField]
    public float AvailableLift;

    /// <summary>Whether the last attempt to touch down was rejected by terrain or an obstacle.</summary>
    [AutoNetworkedField]
    public bool LandingBlocked;
}
