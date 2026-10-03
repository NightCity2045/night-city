// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Combat.Damage;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Combat.Hitscan;

/// <summary>
/// Server-only adapter from a validated vanilla hitscan raycast to the shared NC armor/trauma delivery path.
/// </summary>
public sealed partial class NCHitscanImpactSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _delivery = default!;
    [Dependency] private NCMedicalDamageRoutingSystem _routing = default!;
    [Dependency] private NCTargetingAccuracySystem _accuracy = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMedicalBodyComponent, NCHitscanTraumaRequestedEvent>(OnTraumaRequested);
    }

    private void OnTraumaRequested(
        Entity<NCMedicalBodyComponent> target,
        ref NCHitscanTraumaRequestedEvent args)
    {
        if (args.Raycast.HitEntity != target.Owner)
            return;

        var resolutionId = NCActionResolutionId.New();
        args.Handled = true;
        args.FeedbackDamage = new Content.Shared.Damage.DamageSpecifier();

        if (!_routing.TryPartitionDamage(args.Damage, out var partition))
        {
            Log.Error($"NC hitscan impact on {ToPrettyString(target.Owner)} has no damage routing map; compatibility HP was suppressed.");
            return;
        }

        var systemic = _routing.ApplySystemicSubset(
            target.Owner,
            resolutionId,
            NCSystemicDamageSourceKind.Hitscan,
            0,
            args.Damage);
        if (!systemic.Accepted)
            Log.Error($"NC hitscan systemic payload was rejected on {ToPrettyString(target.Owner)}; compatibility HP was suppressed.");

        foreach (var healing in partition.RejectedHealing)
        {
            var rejected = new NCLegacyHealingRejectedEvent(healing.DamageType, healing.Amount);
            RaiseLocalEvent(target.Owner, ref rejected);
        }

        foreach (var unmapped in partition.Unmapped)
            Log.Warning($"NC hitscan {unmapped.DamageType.Id} change on {ToPrettyString(target.Owner)} has no authoritative mapping; legacy HP was suppressed.");

        if (!partition.LocalTrauma.AnyPositive())
            return;

        ProtoId<NCCombatDamageProfilePrototype> damageProfile;
        if (TryComp<NCHitscanDamageProfileComponent>(args.Hitscan, out var profileOverride))
        {
            damageProfile = profileOverride.Profile;
        }
        else if (!_routing.TryResolveTraumaProfile(partition.LocalTrauma, out damageProfile))
        {
            Log.Error($"NC hitscan {ToPrettyString(args.Hitscan)} has no local profile for its routed trauma; compatibility HP was suppressed.");
            return;
        }

        var selection = args.Raycast.Shooter is { } shooter
            ? _targeting.GetSelection(shooter)
            : new NCTargetingState(NCTargetingMode.Automatic, null);
        var targetTransform = Transform(target.Owner);
        var localHit = _transform.ToCoordinates(
            (target.Owner, targetTransform),
            new MapCoordinates(args.Raycast.HitPosition, targetTransform.MapID)).Position;

        var impact = new NCResolvedCombatImpact(
            resolutionId,
            0,
            0,
            resolutionId.DeriveSeed(),
            target.Owner,
            args.Raycast.Shooter,
            args.Raycast.Gun,
            null,
            args.Raycast.IntendedTarget,
            selection.Mode,
            selection.SelectedZone,
            damageProfile,
            partition.LocalTrauma,
            localHit,
            args.Raycast.ShotDirection,
            0f,
            _timing.CurTime)
        {
            HasPhysicalHitPosition = true,
            ShooterMoving = args.Raycast.Shooter is { } movingShooter && _accuracy.IsMoving(movingShooter),
        };

        if (!_delivery.TryDeliver(impact, out var delivered))
        {
            Log.Error($"NC hitscan delivery rejected an impact on {ToPrettyString(target.Owner)}; compatibility HP was suppressed.");
            return;
        }

        args.FeedbackDamage = delivered.FeedbackDamage;
    }
}
