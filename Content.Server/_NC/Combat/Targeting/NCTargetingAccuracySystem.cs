// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Targeting;

/// <summary>
/// Resolves the server-authoritative movement penalty for deliberate anatomical targeting.
/// </summary>
public sealed partial class NCTargetingAccuracySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private ProtoId<NCTargetingAccuracyProfilePrototype>? _defaultProfile;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfile();
    }

    /// <summary>
    /// Captures local shooter movement when the attack is accepted. Grid motion does not reduce precision.
    /// </summary>
    public bool IsMoving(EntityUid shooter)
    {
        if (!TryGetDefaultProfile(out var profile) ||
            !TryComp<PhysicsComponent>(shooter, out var body))
        {
            return false;
        }

        var threshold = Math.Max(0f, profile.MovementSpeedThreshold);
        return body.LinearVelocity.LengthSquared() > threshold * threshold;
    }

    /// <summary>
    /// Applies the data-driven movement modifier without changing the physical trajectory.
    /// </summary>
    public float ResolveCalledShotHitChance(float baseChance, bool shooterMoving)
    {
        var chance = Math.Clamp(float.IsFinite(baseChance) ? baseChance : 0f, 0f, 1f);
        if (!shooterMoving || !TryGetDefaultProfile(out var profile))
            return chance;

        var multiplier = Math.Clamp(
            float.IsFinite(profile.MovingHitChanceMultiplier) ? profile.MovingHitChanceMultiplier : 0f,
            0f,
            1f);
        return chance * multiplier;
    }

    private bool TryGetDefaultProfile(out NCTargetingAccuracyProfilePrototype profile)
    {
        if (_defaultProfile is { } profileId && _prototypes.TryIndex(profileId, out profile!))
            return true;

        CacheDefaultProfile();
        if (_defaultProfile is { } refreshedId && _prototypes.TryIndex(refreshedId, out profile!))
            return true;

        profile = default!;
        return false;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheDefaultProfile();
    }

    private void CacheDefaultProfile()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCTargetingAccuracyProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();

        _defaultProfile = defaults.Length == 1 ? defaults[0].ID : null;
        if (defaults.Length > 1)
            Log.Error("More than one default human ncTargetingAccuracyProfile is defined.");
    }
}
