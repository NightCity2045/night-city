// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Advances retained cavity fluid through the central medical scheduler and derives its physiological load.
/// The system traverses only the bounded organ container of one scheduled body and never scans the world.
/// </summary>
public sealed partial class NCCavityFluidSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Transplant rejection already consumes these scheduler events through NCMedicalBodyComponent.
        // Bind the cavity pass through the co-located circulation component to keep one directed subscription
        // per component/event pair while remaining inside the same bounded body update.
        SubscribeLocalEvent<NCCirculationComponent, NCMedicalScheduledUpdateEvent>(OnScheduledUpdate);
        SubscribeLocalEvent<NCCirculationComponent, NCMedicalNextUpdateEvent>(OnNextUpdateRequested);
    }

    private void OnScheduledUpdate(Entity<NCCirculationComponent> body, ref NCMedicalScheduledUpdateEvent args)
    {
        AdvanceBody(body.Owner, args.Elapsed);
    }

    private void OnNextUpdateRequested(Entity<NCCirculationComponent> body, ref NCMedicalNextUpdateEvent args)
    {
        if (!HasActiveDrain(body.Owner) ||
            !TryComp<NCCirculationComponent>(body.Owner, out var circulation) ||
            !_prototypes.TryIndex(circulation.Profile, out NCPhysiologyProfilePrototype? profile))
        {
            return;
        }

        var delay = profile.CavityDrainUpdateInterval;
        if (delay > TimeSpan.Zero && (args.Delay == null || delay < args.Delay))
            args.Delay = delay;
    }

    public bool AdvanceBody(EntityUid body, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || !TryGetCavities(body, out var cavities))
            return false;

        var seconds = (float) elapsed.TotalSeconds;
        var changedAny = false;
        foreach (var cavityUid in cavities)
        {
            if (!TryComp<NCCavityFluidComponent>(cavityUid, out var cavity))
                continue;

            Sanitize(cavity);
            var previous = NCCavityFluidMath.GetTotalBloodVolume(cavity);
            var requested = MathF.Max(0f, cavity.DrainRate) * seconds;
            var drained = MathF.Min(previous, requested);
            if (drained > 0f)
            {
                var overflowDrained = MathF.Min(cavity.OverflowBloodVolume, drained);
                cavity.OverflowBloodVolume -= overflowDrained;
                cavity.BloodVolume = MathF.Max(0f, cavity.BloodVolume - (drained - overflowDrained));
            }

            if (NCCavityFluidMath.GetTotalBloodVolume(cavity) <= 0f)
                cavity.DrainRate = 0f;

            Recalculate(cavity);
            var current = NCCavityFluidMath.GetTotalBloodVolume(cavity);
            if (MathHelper.CloseTo(previous, current))
                continue;

            changedAny = true;
            var changed = new NCCavityFluidChangedEvent(cavity.Cavity, previous, current);
            RaiseLocalEvent(cavityUid, ref changed);
        }

        if (changedAny)
        {
            _medicalBodies.TryMarkDirty(
                body,
                NCMedicalDirtyDomains.Vascular |
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Pain |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence,
                true);
        }

        return changedAny;
    }

    public bool TryAccumulate(EntityUid body, EntityUid cavityUid, float amount)
    {
        if (!float.IsFinite(amount) || amount <= 0f ||
            !BelongsToBody(body, cavityUid) ||
            !TryComp<NCCavityFluidComponent>(cavityUid, out var cavity) ||
            !ValidConfiguration(cavity))
        {
            return false;
        }

        Sanitize(cavity);
        var previous = NCCavityFluidMath.GetTotalBloodVolume(cavity);
        var available = MathF.Max(0f, cavity.MaximumBloodVolume - cavity.BloodVolume);
        var retained = MathF.Min(available, amount);
        cavity.BloodVolume += retained;
        cavity.OverflowBloodVolume += amount - retained;
        Recalculate(cavity);

        var changed = new NCCavityFluidChangedEvent(
            cavity.Cavity,
            previous,
            NCCavityFluidMath.GetTotalBloodVolume(cavity));
        RaiseLocalEvent(cavityUid, ref changed);
        return true;
    }

    public bool TryAdjustVolume(EntityUid body, EntityUid cavityUid, float amount)
    {
        if (!float.IsFinite(amount) ||
            !BelongsToBody(body, cavityUid) ||
            !TryComp<NCCavityFluidComponent>(cavityUid, out var cavity) ||
            !ValidConfiguration(cavity))
        {
            return false;
        }

        if (amount > 0f)
            return TryAccumulate(body, cavityUid, amount);

        Sanitize(cavity);
        var previous = NCCavityFluidMath.GetTotalBloodVolume(cavity);
        var removed = MathF.Min(previous, -amount);
        var overflowRemoved = MathF.Min(cavity.OverflowBloodVolume, removed);
        cavity.OverflowBloodVolume -= overflowRemoved;
        cavity.BloodVolume = MathF.Max(0f, cavity.BloodVolume - (removed - overflowRemoved));
        Recalculate(cavity);
        if (MathHelper.CloseTo(previous, NCCavityFluidMath.GetTotalBloodVolume(cavity)))
            return true;

        var changed = new NCCavityFluidChangedEvent(
            cavity.Cavity,
            previous,
            NCCavityFluidMath.GetTotalBloodVolume(cavity));
        RaiseLocalEvent(cavityUid, ref changed);
        return true;
    }

    public bool TrySetDrainRate(EntityUid body, EntityUid cavityUid, float rate)
    {
        if (!float.IsFinite(rate) || rate < 0f ||
            !BelongsToBody(body, cavityUid) ||
            !TryComp<NCCavityFluidComponent>(cavityUid, out var cavity) ||
            !ValidConfiguration(cavity))
        {
            return false;
        }

        cavity.DrainRate = rate;
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence,
            true);
        return true;
    }

    public NCCavityPhysiologyEffects GetPhysiologyEffects(
        EntityUid body,
        NCPhysiologyProfilePrototype profile)
    {
        if (!TryGetCavities(body, out var cavities))
            return NCCavityPhysiologyEffects.None;

        var cardiac = 1f;
        var respiration = 1f;
        var brain = 1f;
        var pain = 0f;
        var shock = 0f;
        foreach (var cavityUid in cavities)
        {
            if (!TryComp<NCCavityFluidComponent>(cavityUid, out var cavity) ||
                !profile.CavityPhysiology.TryGetValue(cavity.Cavity, out var settings))
            {
                continue;
            }

            Recalculate(cavity);
            cardiac *= 1f - Math.Clamp(settings.CardiacOutputPenaltyAtFullCompression, 0f, 1f) *
                       cavity.OrganCompression;
            respiration *= 1f - Math.Clamp(settings.RespiratoryTransferPenaltyAtFullCompression, 0f, 1f) *
                           cavity.OrganCompression;
            brain *= 1f - Math.Clamp(settings.BrainFunctionPenaltyAtFullCompression, 0f, 1f) *
                     cavity.OrganCompression;
            pain += MathF.Max(0f, settings.PainAtFullPressure) * cavity.Pressure;
            shock += MathF.Max(0f, settings.ShockAtFullPressure) * cavity.Pressure;
        }

        return new NCCavityPhysiologyEffects(
            Math.Clamp(cardiac, 0f, 1f),
            Math.Clamp(respiration, 0f, 1f),
            Math.Clamp(brain, 0f, 1f),
            pain,
            shock);
    }

    public bool HasActiveDrain(EntityUid body)
    {
        if (!TryGetCavities(body, out var cavities))
            return false;

        foreach (var cavityUid in cavities)
        {
            if (TryComp<NCCavityFluidComponent>(cavityUid, out var cavity) &&
                cavity.DrainRate > 0f && NCCavityFluidMath.GetTotalBloodVolume(cavity) > 0f)
            {
                return true;
            }
        }

        return false;
    }

    public void ResetBody(EntityUid body)
    {
        if (!TryGetCavities(body, out var cavities))
            return;

        foreach (var cavityUid in cavities)
        {
            if (!TryComp<NCCavityFluidComponent>(cavityUid, out var cavity))
                continue;

            var previous = NCCavityFluidMath.GetTotalBloodVolume(cavity);
            cavity.BloodVolume = 0f;
            cavity.OverflowBloodVolume = 0f;
            cavity.DrainRate = 0f;
            cavity.Pressure = 0f;
            cavity.OrganCompression = 0f;
            if (previous <= 0f)
                continue;

            var changed = new NCCavityFluidChangedEvent(cavity.Cavity, previous, 0f);
            RaiseLocalEvent(cavityUid, ref changed);
        }
    }

    private bool TryGetCavities(EntityUid body, out IReadOnlyList<EntityUid> organs)
    {
        organs = Array.Empty<EntityUid>();
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return false;

        organs = bodyComponent.Organs.ContainedEntities;
        return true;
    }

    private bool BelongsToBody(EntityUid body, EntityUid candidate)
    {
        if (!TryGetCavities(body, out var organs))
            return false;

        foreach (var organ in organs)
        {
            if (organ == candidate)
                return true;
        }

        return false;
    }

    private static bool ValidConfiguration(NCCavityFluidComponent cavity)
    {
        return cavity.Cavity != NCBodyCavity.None &&
               float.IsFinite(cavity.MaximumBloodVolume) && cavity.MaximumBloodVolume > 0f &&
               float.IsFinite(cavity.PressureFullScaleVolume) && cavity.PressureFullScaleVolume > 0f &&
               float.IsFinite(cavity.CompressionFullScaleVolume) && cavity.CompressionFullScaleVolume > 0f;
    }

    private static void Sanitize(NCCavityFluidComponent cavity)
    {
        cavity.BloodVolume = float.IsFinite(cavity.BloodVolume)
            ? Math.Clamp(cavity.BloodVolume, 0f, MathF.Max(0f, cavity.MaximumBloodVolume))
            : 0f;
        cavity.OverflowBloodVolume = float.IsFinite(cavity.OverflowBloodVolume)
            ? MathF.Max(0f, cavity.OverflowBloodVolume)
            : 0f;
        cavity.DrainRate = float.IsFinite(cavity.DrainRate) ? MathF.Max(0f, cavity.DrainRate) : 0f;
    }

    private static void Recalculate(NCCavityFluidComponent cavity)
    {
        var total = NCCavityFluidMath.GetTotalBloodVolume(cavity);
        cavity.Pressure = Math.Clamp(total / cavity.PressureFullScaleVolume, 0f, 1f);
        cavity.OrganCompression = Math.Clamp(total / cavity.CompressionFullScaleVolume, 0f, 1f);
    }
}
