// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Targeting;

/// <summary>
/// Data-driven accuracy penalties applied while deliberately targeting an anatomical zone.
/// </summary>
[Prototype("ncTargetingAccuracyProfile")]
public sealed partial class NCTargetingAccuracyProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Exactly one profile supplies the default firearm targeting rules for human shooters.
    /// </summary>
    [DataField]
    public bool DefaultForHumans { get; private set; }

    /// <summary>
    /// Minimum local movement speed at which deliberate anatomical targeting becomes unstable.
    /// Local velocity avoids penalizing a stationary shooter merely because their grid is moving.
    /// </summary>
    [DataField]
    public float MovementSpeedThreshold { get; private set; } = 0.1f;

    /// <summary>
    /// Multiplier applied to the selected-zone hit chance while the shooter is moving.
    /// Movement affects anatomical precision only; physical projectile direction remains unchanged.
    /// </summary>
    [DataField]
    public float MovingHitChanceMultiplier { get; private set; } = 0.75f;
}
