// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._NC.Combat.Targeting;

/// <summary>
/// Applies the server-authoritative trajectory penalty for anatomical targeting while moving.
/// </summary>
public sealed partial class NCTargetingAccuracySystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;

    private ProtoId<NCTargetingAccuracyProfilePrototype>? _defaultProfile;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfile();
    }

    /// <summary>
    /// Returns the authoritative base trajectory after applying any movement penalty.
    /// Automatic targeting and stationary deliberate shots retain their original direction.
    /// </summary>
    public Angle ApplyMovementSpread(EntityUid shooter, Angle direction)
    {
        var selection = _targeting.GetSelection(shooter);
        if (selection.Mode != NCTargetingMode.SelectedZone ||
            !TryGetDefaultProfile(out var profile) ||
            !TryComp<PhysicsComponent>(shooter, out var body))
        {
            return direction;
        }

        var threshold = Math.Max(0f, profile.MovementSpeedThreshold);
        if (body.LinearVelocity.LengthSquared() <= threshold * threshold)
            return direction;

        var spread = Math.Max(0f, (float) profile.MovingSpread.Theta);
        if (spread <= 0f || !float.IsFinite(spread))
            return direction;

        var halfSpread = new Angle(spread / 2f);
        return direction + _random.NextAngle(-halfSpread, halfSpread);
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
