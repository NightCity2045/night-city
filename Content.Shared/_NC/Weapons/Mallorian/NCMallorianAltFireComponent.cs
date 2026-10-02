// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Actions;
using Robust.Shared.Audio;

namespace Content.Shared._NC.Weapons.Mallorian;

/// <summary>
/// Data-only configuration for the Mallorian's magazine-dump alternative fire.
/// The server briefly enables <see cref="VolleyActive"/> while the stock gun pipeline resolves the shot.
/// </summary>
[RegisterComponent]
public sealed partial class NCMallorianAltFireComponent : Component
{
    /// <summary>
    /// Total angular width of the volley. Every remaining cartridge receives one trajectory in this cone.
    /// </summary>
    [DataField]
    public Angle Spread = Angle.FromDegrees(20);

    /// <summary>
    /// Sound used once for the complete volley instead of once per cartridge.
    /// </summary>
    [DataField]
    public SoundSpecifier VolleySound = new SoundPathSpecifier("/Audio/Weapons/Guns/Gunshots/shotgun.ogg");

    /// <summary>
    /// Transient server-authoritative context consumed by the stock gun pipeline.
    /// It is never serialized because it only exists for the duration of one synchronous shot.
    /// </summary>
    [ViewVariables]
    public bool VolleyActive;
}

/// <summary>
/// Targeted action request for the Mallorian magazine-dump shot.
/// </summary>
public sealed partial class NCMallorianAltFireActionEvent : WorldTargetActionEvent;
