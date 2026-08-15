// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Combat.Melee;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Melee;

/// <summary>
/// Converts contacts accepted by vanilla melee validation into exactly one zonal armor/trauma delivery per target.
/// It contains no hit roll, RED check, skill modifier, spatial scan or global HP mutation.
/// </summary>
public sealed partial class NCMeleeAttackAdapterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private ProtoId<NCMeleeDamageProfileMapPrototype>? _defaultProfileMap;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        CacheDefaultProfileMap();
    }

    /// <summary>
    /// Snapshots private intent after vanilla has accepted the swing and cancellable melee events did not handle it.
    /// A wide attack passes no intended target, so its physical contacts resolve automatic zones independently.
    /// </summary>
    public NCActionResolutionId BeginAttack(EntityUid user, EntityUid weapon, EntityUid? intendedTarget)
    {
        var selection = _targeting.GetSelection(user);
        var context = EnsureComp<NCMeleeAttackContextComponent>(weapon);
        context.ResolutionId = NCActionResolutionId.New();
        context.User = user;
        context.IntendedTarget = intendedTarget;
        context.TargetingMode = selection.Mode;
        context.SelectedZone = selection.SelectedZone;
        context.NextImpactSequence = 0;
        context.ResolvedTargets.Clear();
        return context.ResolutionId;
    }

    public void EndAttack(EntityUid user, EntityUid weapon)
    {
        if (TryComp<NCMeleeAttackContextComponent>(weapon, out var context) && context.User == user)
            RemComp<NCMeleeAttackContextComponent>(weapon);
    }

    /// <summary>
    /// Returns true whenever the target is an NC medical body. Even an invalid content mapping must not fall back
    /// to compatibility HP; such authoring errors are made safe and reported instead.
    /// </summary>
    public bool TryApplyImpact(
        EntityUid user,
        EntityUid weapon,
        EntityUid target,
        DamageSpecifier damage,
        Vector2? attackDirection,
        out DamageSpecifier feedbackDamage)
    {
        feedbackDamage = new DamageSpecifier();
        if (!HasComp<NCMedicalBodyComponent>(target))
            return false;

        if (!TryComp<NCMeleeAttackContextComponent>(weapon, out var context) ||
            context.User != user ||
            !context.ResolutionId.IsValid)
        {
            Log.Error($"NC melee contact for {ToPrettyString(target)} had no accepted attack context; compatibility HP was suppressed.");
            return true;
        }

        // A malicious repeated entity in a wide-attack payload cannot apply the same physical contact twice.
        if (!context.ResolvedTargets.Add(target))
            return true;

        var impactSequence = context.NextImpactSequence++;
        if (!_routing.TryPartitionDamage(damage, out var partition))
        {
            Log.Error($"NC melee contact from {ToPrettyString(weapon)} has no damage routing map; compatibility HP was suppressed.");
            return true;
        }

        var systemic = _routing.ApplySystemicSubset(
            target,
            context.ResolutionId,
            NCSystemicDamageSourceKind.Melee,
            impactSequence,
            damage);
        if (!systemic.Accepted)
            Log.Error($"NC melee systemic payload was rejected on {ToPrettyString(target)}; compatibility HP was suppressed.");

        foreach (var healing in partition.RejectedHealing)
        {
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target, ref rejected);
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC melee {unmapped.DamageType.Id} change on {ToPrettyString(target)} has no authoritative mapping; legacy HP was suppressed.");

        if (!partition.LocalTrauma.AnyPositive())
            return true;

        if (!TryResolveDamageProfile(weapon, partition.LocalTrauma, out var profile))
        {
            Log.Error($"NC melee contact from {ToPrettyString(weapon)} has no local profile for its routed trauma; compatibility HP was suppressed.");
            return true;
        }

        var direction = attackDirection ??
                        (_transform.GetWorldPosition(target) - _transform.GetWorldPosition(user));
        var localHit = _transform.GetRelativePosition(Transform(user), target);
        var contamination = TryComp<NCMeleeDamageProfileComponent>(weapon, out var profileOverride)
            ? profileOverride.Contamination
            : 0f;
        var impact = new NCResolvedCombatImpact(
            context.ResolutionId,
            impactSequence,
            0,
            context.ResolutionId.DeriveSeed(impactSequence),
            target,
            user,
            weapon,
            null,
            context.IntendedTarget,
            context.TargetingMode,
            context.SelectedZone,
            profile,
            partition.LocalTrauma,
            localHit,
            direction,
            contamination,
            _timing.CurTime);

        if (!_delivery.TryDeliver(impact, out var delivery))
        {
            Log.Error($"NC melee delivery rejected an impact on {ToPrettyString(target)}; compatibility HP was suppressed.");
            return true;
        }

        feedbackDamage = delivery.FeedbackDamage;
        return true;
    }

    /// <summary>
    /// Resolves the optional exact override first, otherwise the highest positive mapped damage entry.
    /// Lexical tie-breaking makes mixed payloads deterministic across runtimes.
    /// </summary>
    public bool TryResolveDamageProfile(
        EntityUid weapon,
        DamageSpecifier damage,
        out ProtoId<NCCombatDamageProfilePrototype> profile)
    {
        if (TryComp<NCMeleeDamageProfileComponent>(weapon, out var profileOverride) &&
            _prototypes.HasIndex(profileOverride.Profile))
        {
            profile = profileOverride.Profile;
            return true;
        }

        if (_defaultProfileMap is not { } mapId ||
            !_prototypes.TryIndex(mapId, out NCMeleeDamageProfileMapPrototype? map))
        {
            CacheDefaultProfileMap();
            if (_defaultProfileMap is not { } refreshedId ||
                !_prototypes.TryIndex(refreshedId, out map))
            {
                profile = default;
                return false;
            }
        }

        var bestAmount = FixedPoint2.Zero;
        ProtoId<Content.Shared.Damage.Prototypes.DamageTypePrototype>? bestType = null;
        ProtoId<NCCombatDamageProfilePrototype> bestProfile = default;
        foreach (var (damageType, amount) in damage.DamageDict)
        {
            if (amount <= FixedPoint2.Zero ||
                !map.Profiles.TryGetValue(damageType, out var mappedProfile) ||
                !_prototypes.HasIndex(mappedProfile))
            {
                continue;
            }

            if (amount < bestAmount ||
                amount == bestAmount && bestType is { } current &&
                StringComparer.Ordinal.Compare(damageType.Id, current.Id) >= 0)
            {
                continue;
            }

            bestAmount = amount;
            bestType = damageType;
            bestProfile = mappedProfile;
        }

        profile = bestProfile;
        return bestType != null;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        CacheDefaultProfileMap();
    }

    private void CacheDefaultProfileMap()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCMeleeDamageProfileMapPrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToArray();

        if (defaults.Length != 1)
        {
            _defaultProfileMap = null;
            if (defaults.Length > 1)
                Log.Error("More than one default human ncMeleeDamageProfileMap is defined.");
            return;
        }

        _defaultProfileMap = defaults[0].ID;
    }
}
