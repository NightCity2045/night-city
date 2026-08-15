// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Projectiles;
using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Snapshots private targeting intent onto each projectile after the physical gun pipeline accepts a shot.
/// </summary>
public sealed partial class NCProjectileContextSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;

    private ProtoId<NCProjectileDamageProfileMapPrototype>? _defaultProfileMap;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GunComponent, AmmoShotEvent>(OnAmmoShot);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfileMap();
    }

    /// <summary>
    /// Called by the narrow server gun adapter only after cancellable pre-shot events have succeeded.
    /// </summary>
    public void PrepareShot(Entity<GunComponent> gun, EntityUid? shooter)
    {
        var selection = shooter is { } shooterUid
            ? _targeting.GetSelection(shooterUid)
            : new Content.Shared._NC.Combat.Targeting.NCTargetingState(
                Content.Shared._NC.Combat.Targeting.NCTargetingMode.Automatic,
                null);

        var pending = EnsureComp<NCPendingShotContextComponent>(gun);
        pending.ResolutionId = NCActionResolutionId.New();
        pending.Shooter = shooter;
        pending.Weapon = gun.Owner;
        pending.IntendedTarget = gun.Comp.Target;
        pending.TargetingMode = selection.Mode;
        pending.SelectedZone = selection.SelectedZone;
        pending.CreatedAt = _timing.CurTime;
    }

    private void OnAmmoShot(Entity<GunComponent> gun, ref AmmoShotEvent args)
    {
        if (!TryComp<NCPendingShotContextComponent>(gun, out var pending))
            return;

        // A stale pending component can only come from an interrupted external firing path; never reuse its intent.
        if (_timing.CurTime - pending.CreatedAt > TimeSpan.FromSeconds(1))
        {
            RemCompDeferred<NCPendingShotContextComponent>(gun);
            return;
        }

        var projectilesInShot = (uint) args.FiredProjectiles.Count;
        uint sequence = 0;
        foreach (var projectile in args.FiredProjectiles)
        {
            if (TerminatingOrDeleted(projectile) ||
                !TryComp<ProjectileComponent>(projectile, out var projectileComponent) ||
                !projectileComponent.Damage.DamageDict.Values.Any(amount => amount != Content.Shared.FixedPoint.FixedPoint2.Zero))
            {
                sequence++;
                continue;
            }

            // A systemic-only or not-yet-mapped payload still needs the immutable physical shot correlation.
            // Semantic partitioning happens exactly once at the real collision, where the struck body is known.
            var context = EnsureComp<NCProjectileHitContextComponent>(projectile);
            context.ResolutionId = pending.ResolutionId;
            context.ProjectileSequence = sequence;
            context.ProjectilesInShot = Math.Max(1u, projectilesInShot);
            context.NextImpactSequence = 0;
            context.Shooter = pending.Shooter;
            context.Weapon = pending.Weapon;
            context.IntendedTarget = pending.IntendedTarget;
            context.TargetingMode = pending.TargetingMode;
            context.SelectedZone = pending.SelectedZone;
            context.Seed = pending.ResolutionId.DeriveSeed(sequence);
            sequence++;
        }

        RemCompDeferred<NCPendingShotContextComponent>(gun);
    }

    /// <summary>
    /// Resolves an exact ammunition override first, then the default dominant-damage fallback. Invalid explicit
    /// content never silently changes semantics by falling through to a different profile.
    /// </summary>
    public bool TryResolveDamageProfile(
        EntityUid projectile,
        DamageSpecifier damage,
        out ProtoId<NCCombatDamageProfilePrototype> profile)
    {
        // A utility projectile may still carry collision subscribers, but zero damage never creates medical context.
        if (!damage.AnyPositive())
        {
            profile = default;
            return false;
        }

        if (TryComp<NCProjectileDamageProfileComponent>(projectile, out var profileOverride))
        {
            profile = profileOverride.Profile;
            return _prototypes.HasIndex(profile);
        }

        if (_defaultProfileMap is not { } mapId ||
            !_prototypes.TryIndex(mapId, out NCProjectileDamageProfileMapPrototype? map))
        {
            CacheDefaultProfileMap();
            if (_defaultProfileMap is not { } refreshedId ||
                !_prototypes.TryIndex(refreshedId, out map))
            {
                profile = default;
                return false;
            }
        }

        return NCProjectileDamageProfileRules.TryResolve(map, damage, out profile) &&
               _prototypes.HasIndex(profile);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheDefaultProfileMap();
    }

    private void CacheDefaultProfileMap()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCProjectileDamageProfileMapPrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();

        if (defaults.Length != 1)
        {
            _defaultProfileMap = null;
            if (defaults.Length > 1)
                Log.Error("More than one default human ncProjectileDamageProfileMap is defined.");
            return;
        }

        _defaultProfileMap = defaults[0].ID;
    }
}
