// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Server.Damage.Systems;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Environment;

/// <summary>
/// Authoritative compatibility boundary for damage sources that have not yet supplied a richer physical context.
/// It catches vanilla environment, explosion, thrown, collision and scripted damage without mutating global HP.
/// </summary>
public sealed partial class NCEnvironmentDamageAdapterSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Godmode and equivalent policy blockers must cancel before this adapter can create a medical wound.
        SubscribeLocalEvent<NCMedicalBodyComponent, BeforeDamageChangedEvent>(
            OnBeforeDamageChanged,
            after: [typeof(GodmodeSystem)]);
    }

    /// <summary>
    /// Resolves the default content map without exposing mutable runtime state to source systems.
    /// </summary>
    public bool TryResolveDamageProfile(
        DamageSpecifier damage,
        out ProtoId<NCCombatDamageProfilePrototype> profile)
    {
        return _routing.TryResolveTraumaProfile(damage, out profile);
    }

    public bool TryResolveSystemicProfile(
        ProtoId<Content.Shared.Damage.Prototypes.DamageTypePrototype> damageType,
        out ProtoId<NCSystemicDamageProfilePrototype> profile)
    {
        return _routing.TryResolveSystemicProfile(damageType, out profile);
    }

    private void OnBeforeDamageChanged(
        Entity<NCMedicalBodyComponent> target,
        ref BeforeDamageChangedEvent args)
    {
        // An earlier policy system such as godmode owns the cancellation and must not be bypassed.
        if (args.Cancelled)
            return;

        // Damageable is never authoritative for an NC body. Both damage and healing are redirected before
        // compatibility HP can change, so an unsupported source can never silently become the death authority.
        args.Cancelled = true;
        if (!_routing.TryPartitionDamage(args.Damage, out var partition))
        {
            Log.Error($"NC compatibility damage on {ToPrettyString(target.Owner)} has no default environment map; legacy HP was suppressed.");
            return;
        }

        var resolutionId = NCActionResolutionId.New();
        var systemicResult = _routing.ApplySystemicSubset(
            target.Owner,
            resolutionId,
            NCSystemicDamageSourceKind.Environment,
            0,
            args.Damage);
        if (!systemicResult.Accepted)
        {
            Log.Error($"NC systemic compatibility payload was rejected on {ToPrettyString(target.Owner)}; legacy HP was suppressed.");
        }

        foreach (var healing in partition.RejectedHealing)
        {
            // Generic negative physical damage has no sufficient anatomy/treatment context. It is explicitly
            // rejected here; trusted Rejuvenate and typed treatment/surgery APIs own actual repair.
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target.Owner, ref rejected);
        }

        // Only mapped local entries are delivered as trauma. A Heat+Radiation payload therefore creates a
        // Heat wound from Heat alone while Radiation independently enters the systemic state.
        var localDamage = partition.LocalTrauma;
        if (localDamage.AnyPositive() && TryResolveDamageProfile(localDamage, out var profile))
        {
            EntityUid? origin = args.Origin is { } candidate && Exists(candidate)
                ? candidate
                : null;
            var direction = ResolveServerDirection(target.Owner, origin);

            // A BeforeDamageChangedEvent has no trustworthy contact point. The target origin is therefore the explicit
            // fallback while the server-derived source direction and weighted automatic zone preserve determinism.
            var impact = new NCResolvedCombatImpact(
                resolutionId,
                0,
                0,
                resolutionId.DeriveSeed(),
                target.Owner,
                origin,
                null,
                null,
                null,
                NCTargetingMode.Automatic,
                null,
                profile,
                localDamage,
                Vector2.Zero,
                direction,
                0f,
                _timing.CurTime);

            if (!_delivery.TryDeliver(impact, out _))
                Log.Error($"NC compatibility trauma delivery rejected damage on {ToPrettyString(target.Owner)}; legacy HP was suppressed.");
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC compatibility {unmapped.DamageType.Id} change on {ToPrettyString(target.Owner)} has no authoritative mapping; legacy HP was suppressed.");
    }

    private Vector2 ResolveServerDirection(EntityUid target, EntityUid? origin)
    {
        if (origin is not { } originUid)
        {
            return Vector2.Zero;
        }

        // Every live entity has a transform; Exists was checked before the nullable origin reached this method.
        var originTransform = Transform(originUid);
        var targetTransform = Transform(target);
        if (originTransform.MapID != targetTransform.MapID)
            return Vector2.Zero;

        return _transform.GetWorldPosition(targetTransform) -
               _transform.GetWorldPosition(originTransform);
    }

}
