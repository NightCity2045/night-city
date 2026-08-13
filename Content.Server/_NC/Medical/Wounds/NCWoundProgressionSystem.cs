// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Systemic;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Wounds;

/// <summary>
/// Advances only one scheduler-selected body's bounded wound records. Infection, healing, reopening,
/// dressing saturation and tourniquet ischemia are deterministic prototype-driven transitions; this system
/// never scans the world and never performs RED, skill or random checks.
/// </summary>
public sealed partial class NCWoundProgressionSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private NCTreatmentSystem _treatment = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;

    private const float FloatTolerance = 0.000001f;

    public override void Initialize()
    {
        base.Initialize();

        // Pharmacology clears/metabolizes first, so infection consumes one unambiguous end-of-boundary
        // antibiotic/immunosuppression state instead of depending on subscription registration order.
        SubscribeLocalEvent<NCWoundContainerComponent, NCMedicalScheduledUpdateEvent>(
            OnScheduledUpdate,
            after: [typeof(NCPharmacologySystem)]);
        SubscribeLocalEvent<NCWoundContainerComponent, MoveEvent>(OnBodyMoved);
        SubscribeLocalEvent<NCWoundContainerComponent, NCWoundCreatedEvent>(OnWoundCreated);
    }

    private void OnScheduledUpdate(
        Entity<NCWoundContainerComponent> entity,
        ref NCMedicalScheduledUpdateEvent args)
    {
        AdvanceBody(entity.Owner, args.Elapsed);
    }

    private void OnBodyMoved(Entity<NCWoundContainerComponent> entity, ref MoveEvent args)
    {
        if (args.ParentChanged || args.OnlyRotation || entity.Comp.Wounds.Count == 0 ||
            !TryGetProfiles(entity.Comp, out _, out var progression))
        {
            return;
        }

        var distance = (args.NewPosition.Position - args.OldPosition.Position).Length();
        if (!float.IsFinite(distance) || distance <= 0f)
            return;

        // Transform events remain O(1). The central body update consumes this scalar once for all wounds.
        entity.Comp.PendingMovementDistance = Math.Clamp(
            entity.Comp.PendingMovementDistance + distance,
            0f,
            progression.MaximumPendingMovementDistance);
    }

    private void OnWoundCreated(Entity<NCWoundContainerComponent> entity, ref NCWoundCreatedEvent args)
    {
        if (!TryGetProfiles(entity.Comp, out _, out var progression) ||
            !progression.ImpactReopenStressBySeverity.TryGetValue(args.Severity, out var stress) ||
            stress <= 0f)
        {
            return;
        }

        // A new impact in the same target zone can tear an older closure. The newly created wound is
        // excluded by its stable ID and the actual transition remains deferred to the scheduler boundary.
        foreach (var wound in entity.Comp.Wounds.Values)
        {
            if (wound.WoundId == args.WoundId || wound.TargetZone != args.TargetZone ||
                wound.ClosureState is NCWoundClosureState.Open or NCWoundClosureState.Healed)
            {
                continue;
            }

            wound.ReopenStress = MathF.Max(0f, wound.ReopenStress + stress);
        }
    }

    /// <summary>
    /// Explicit deterministic entry point for the scheduler and tests.
    /// <paramref name="timestamp"/> is the interval end; acute state never advances while dematerialized.
    /// </summary>
    public NCWoundProgressionResult AdvanceBody(
        EntityUid body,
        TimeSpan elapsed,
        TimeSpan? timestamp = null)
    {
        if (elapsed < TimeSpan.Zero || !HasComp<NCMedicalBodyComponent>(body) ||
            !TryComp<NCWoundContainerComponent>(body, out var container) ||
            !TryGetProfiles(container, out _, out var progression))
        {
            return default;
        }

        var now = timestamp ?? _timing.CurTime;
        var seconds = (float) Math.Max(0d, elapsed.TotalSeconds);
        if (!float.IsFinite(seconds))
            return default;

        TryComp<NCTreatmentStateComponent>(body, out var treatmentState);
        NCTreatmentProfilePrototype? treatmentProfile = null;
        if (treatmentState != null && !string.IsNullOrWhiteSpace(treatmentState.Profile.Id))
            _prototypes.TryIndex(treatmentState.Profile, out treatmentProfile);
        TryComp<NCBleedingComponent>(body, out var bleeding);
        var pharmacology = _pharmacology.GetModifiers(body);

        var changed = false;
        var saturatedBandages = 0;
        var ischemicRegions = 0;
        var reopenedCount = 0;
        var scarredCount = 0;
        HashSet<Guid>? refreshSources = null;
        HashSet<Guid>? resolvedWounds = null;
        List<NCWoundProgressedEvent>? progressionEvents = null;

        if (seconds > 0f && treatmentState != null && treatmentProfile != null && bleeding != null)
        {
            saturatedBandages = AdvanceBandages(
                container,
                treatmentState,
                treatmentProfile,
                bleeding,
                seconds,
                now,
                ref refreshSources);
            changed |= saturatedBandages > 0 || treatmentState.WoundTreatments.Values.Any(record =>
                (record.AppliedFlags & NCWoundTreatmentFlags.Bandaged) != 0);

            ischemicRegions = AdvanceTourniquetIschemia(
                body,
                container,
                treatmentState,
                treatmentProfile,
                progression,
                elapsed,
                now);
            changed |= ischemicRegions > 0;
        }

        var movementStress = container.PendingMovementDistance * progression.MovementStressPerTile;
        if (container.PendingMovementDistance > 0f)
        {
            container.PendingMovementDistance = 0f;
            changed = true;
        }

        foreach (var wound in container.Wounds.Values)
        {
            if (!_prototypes.TryIndex(wound.Prototype, out NCWoundPrototype? woundPrototype) ||
                !IsValid(woundPrototype))
                continue;

            var previousStage = wound.Stage;
            var previousInfectionStage = wound.InfectionStage;
            var outcome = AdvanceWound(
                body,
                wound,
                woundPrototype,
                progression,
                treatmentState,
                treatmentProfile,
                pharmacology,
                movementStress,
                seconds,
                now);
            changed |= outcome.Changed;
            if (outcome.SourceChanged)
                (refreshSources ??= []).Add(wound.WoundId);
            if (outcome.Reopened)
                reopenedCount++;
            if (outcome.Resolved)
            {
                (resolvedWounds ??= []).Add(wound.WoundId);
                AddScar(container, wound, progression, now);
                scarredCount++;
            }

            if (previousStage != wound.Stage || previousInfectionStage != wound.InfectionStage ||
                outcome.Reopened || outcome.Resolved)
            {
                (progressionEvents ??= []).Add(new NCWoundProgressedEvent(
                    wound.WoundId,
                    previousStage,
                    wound.Stage,
                    previousInfectionStage,
                    wound.InfectionStage,
                    outcome.Reopened,
                    outcome.Resolved));
            }
        }

        if (seconds > 0f)
        {
            var sepsisRate = 0f;
            foreach (var wound in container.Wounds.Values)
            {
                if (progression.SepsisExposurePerSecondByStage.TryGetValue(
                        wound.InfectionStage,
                        out var stageRate))
                {
                    sepsisRate += stageRate * Math.Clamp(wound.Infection, 0f, 1f);
                }
            }

            sepsisRate -= pharmacology.AntibioticPower *
                          progression.SepsisAntibioticRecoveryPerPowerPerSecond;
            if (!MathHelper.CloseTo(sepsisRate, 0f))
            {
                var sepsis = _systemic.Apply(
                    body,
                    progression.SepsisSystemicProfile,
                    sepsisRate * seconds);
                changed |= sepsis.Accepted && !MathHelper.CloseTo(
                    sepsis.PreviousBurden,
                    sepsis.CurrentBurden);
            }
        }

        if (refreshSources != null)
        {
            foreach (var woundId in refreshSources)
            {
                if (container.Wounds.TryGetValue(woundId, out var wound) &&
                    (resolvedWounds == null || !resolvedWounds.Contains(woundId)))
                {
                    _bleeding.SynchronizeWoundSources(body, wound);
                }
            }

            _treatment.RefreshDerivedSources(body, now);
        }

        if (resolvedWounds != null)
        {
            _bleeding.RemoveWoundSources(body, resolvedWounds);
            foreach (var woundId in resolvedWounds)
            {
                container.Wounds.Remove(woundId);
                treatmentState?.WoundTreatments.Remove(woundId);
            }
            changed = true;
        }

        if (!changed)
            return new NCWoundProgressionResult(true, false, 0, 0, 0, 0);

        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
        container.LastProgressionPersistenceEventAt = now;

        if (progressionEvents != null)
        {
            foreach (var progressed in progressionEvents)
            {
                var raised = progressed;
                RaiseLocalEvent(body, ref raised);
            }
        }

        return new NCWoundProgressionResult(
            true,
            true,
            reopenedCount,
            scarredCount,
            saturatedBandages,
            ischemicRegions);
    }

    private int AdvanceBandages(
        NCWoundContainerComponent wounds,
        NCTreatmentStateComponent treatment,
        NCTreatmentProfilePrototype profile,
        NCBleedingComponent bleeding,
        float seconds,
        TimeSpan now,
        ref HashSet<Guid>? refreshSources)
    {
        var saturated = 0;
        var intervalStart = now - TimeSpan.FromSeconds(seconds);
        foreach (var (woundId, record) in treatment.WoundTreatments)
        {
            if ((record.AppliedFlags & NCWoundTreatmentFlags.Bandaged) == 0 ||
                !wounds.Wounds.TryGetValue(woundId, out var wound))
            {
                continue;
            }

            var absorbedRate = 0f;
            foreach (var source in bleeding.Sources)
            {
                if (source.WoundId != woundId || source.Channel == NCBleedingChannel.Internal)
                    continue;
                absorbedRate += MathF.Max(0f, source.BaseRate - source.CurrentRate);
            }

            // A treatment applied at this boundary must not absorb blood from the interval before it existed.
            // This matters when a treatment action wakes a body whose previous physiology tick was seconds ago.
            var activeSeconds = OverlapSeconds(intervalStart, now, record.BandagedAt, now);
            record.BandageAbsorptionRemaining = MathF.Max(
                0f,
                record.BandageAbsorptionRemaining - absorbedRate * activeSeconds);
            record.BandageSterility = Math.Clamp(
                record.BandageSterility - profile.BandageSterilityDecayPerSecond * activeSeconds,
                0f,
                1f);
            record.ModifiedAt = now;
            if (record.BandageAbsorptionRemaining > FloatTolerance)
                continue;

            record.AppliedFlags &= ~NCWoundTreatmentFlags.Bandaged;
            wound.TreatmentFlags &= ~NCWoundTreatmentFlags.Bandaged;
            wound.Contamination = Math.Clamp(
                wound.Contamination + profile.SaturatedBandageContamination,
                0f,
                1f);
            if (wound.ClosureState == NCWoundClosureState.Open &&
                wound.Stage is NCWoundStage.Treated or NCWoundStage.Closed)
            {
                wound.Stage = NCWoundStage.Open;
            }
            wound.ModifiedAt = now;
            (refreshSources ??= []).Add(woundId);
            saturated++;
        }

        return saturated;
    }

    private int AdvanceTourniquetIschemia(
        EntityUid body,
        NCWoundContainerComponent wounds,
        NCTreatmentStateComponent treatment,
        NCTreatmentProfilePrototype treatmentProfile,
        NCWoundProgressionProfilePrototype progression,
        TimeSpan elapsed,
        TimeSpan now)
    {
        var intervalStart = now - elapsed;
        var affectedRegions = 0;
        foreach (var (root, tourniquet) in treatment.Tourniquets)
        {
            if (!tourniquet.Active)
                continue;

            var riskSeconds = OverlapSeconds(
                intervalStart,
                now,
                tourniquet.IschemiaRiskAt,
                tourniquet.CriticalIschemiaAt);
            var criticalSeconds = OverlapSeconds(
                intervalStart,
                now,
                tourniquet.CriticalIschemiaAt,
                TimeSpan.MaxValue);
            if (riskSeconds <= 0f && criticalSeconds <= 0f)
                continue;

            var integrityDamage = riskSeconds * progression.IschemiaIntegrityDamagePerSecond +
                                  criticalSeconds * progression.CriticalIschemiaIntegrityDamagePerSecond;
            var nerveDamage = riskSeconds * progression.IschemiaNerveDamagePerSecond +
                              criticalSeconds * progression.CriticalIschemiaNerveDamagePerSecond;
            var necrosis = riskSeconds * progression.IschemiaNecrosisPerSecond +
                           criticalSeconds * progression.CriticalIschemiaNecrosisPerSecond;

            var categories = new List<ProtoId<OrganCategoryPrototype>>(4);
            foreach (var (zone, candidateRoot) in treatmentProfile.TourniquetZoneRoots)
            {
                if (candidateRoot != root || !_prototypes.TryIndex(zone, out NCTargetZonePrototype? zoneProfile) ||
                    categories.Contains(zoneProfile.MedicalRegionCategory))
                {
                    continue;
                }
                categories.Add(zoneProfile.MedicalRegionCategory);
            }

            foreach (var category in categories)
            {
                if (!_anatomy.TryGetOrgan(body, category, out var region))
                    continue;

                if (TryComp<NCOrganIntegrityComponent>(region, out var integrity) &&
                    integrity.Material == NCAnatomicalMaterialKind.Organic)
                {
                    integrity.CurrentIntegrity = Math.Clamp(
                        integrity.CurrentIntegrity - integrityDamage,
                        0f,
                        MathF.Max(0f, integrity.MaxIntegrity));
                    integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
                        ? integrity.CurrentIntegrity / integrity.MaxIntegrity
                        : 0f;
                    integrity.Necrosis = Math.Clamp(integrity.Necrosis + necrosis, 0f, 1f);
                }

                if (TryComp<NCNerveComponent>(region, out var nerve))
                {
                    nerve.NeuralIntegrity = Math.Clamp(nerve.NeuralIntegrity - nerveDamage, 0f, 1f);
                    nerve.MotorFunction = Math.Clamp(nerve.MotorFunction - nerveDamage, 0f, 1f);
                }
                affectedRegions++;
            }

            foreach (var wound in wounds.Wounds.Values)
            {
                if (treatmentProfile.TourniquetZoneRoots.TryGetValue(wound.TargetZone, out var woundRoot) &&
                    woundRoot == root)
                {
                    wound.Necrosis = Math.Clamp(wound.Necrosis + necrosis, 0f, 1f);
                }
            }
        }

        return affectedRegions;
    }

    private WoundAdvanceOutcome AdvanceWound(
        EntityUid body,
        NCWoundRecord wound,
        NCWoundPrototype woundPrototype,
        NCWoundProgressionProfilePrototype progression,
        NCTreatmentStateComponent? treatment,
        NCTreatmentProfilePrototype? treatmentProfile,
        NCPharmacologyModifiers pharmacology,
        float movementStress,
        float seconds,
        TimeSpan now)
    {
        var changed = false;
        var sourceChanged = false;
        var reopened = false;
        var age = wound.Age + TimeSpan.FromSeconds(seconds);
        if (age != wound.Age)
        {
            wound.Age = age;
            changed = true;
        }

        InitializeLongitudinalBaselines(wound);
        if (wound.Stage == NCWoundStage.Fresh && wound.Age >= progression.FreshToOpenDelay)
        {
            wound.Stage = NCWoundStage.Open;
            changed = true;
        }

        var bandageSterility = GetBandageSterility(wound.WoundId, treatment);
        if (seconds > 0f && wound.ClosureState == NCWoundClosureState.Open &&
            wound.Stage != NCWoundStage.Necrotic)
        {
            var contaminationDelta = progression.OpenContaminationPerSecond *
                                     (1f - bandageSterility) * seconds;
            var contaminationDecay = bandageSterility *
                                     progression.SterileContaminationDecayPerSecond * seconds;
            var contamination = Math.Clamp(
                wound.Contamination + contaminationDelta - contaminationDecay,
                0f,
                1f);
            changed |= SetIfDifferent(ref wound.Contamination, contamination);
        }

        if (seconds > 0f)
        {
            var contaminationExposure = progression.InfectionContaminationThreshold >= 1f
                ? 0f
                : Math.Clamp(
                    (wound.Contamination - progression.InfectionContaminationThreshold) /
                    (1f - progression.InfectionContaminationThreshold),
                    0f,
                    1f);
            var severityMultiplier = progression.InfectionSeverityMultipliers[wound.Severity];
            var infectionClosureMultiplier = wound.ClosureState == NCWoundClosureState.Open
                ? 1f
                : progression.ClosedInfectionMultiplier;
            var embeddedMultiplier = wound.EmbeddedObjectId == null
                ? 1f
                : progression.EmbeddedObjectInfectionMultiplier;
            var immunosuppressionMultiplier = 1f +
                MathF.Max(0f, pharmacology.Immunosuppression) *
                progression.ImmunosuppressionInfectionMultiplier;
            var infection = wound.Infection + progression.InfectionGrowthPerSecond *
                woundPrototype.InfectionSusceptibility * severityMultiplier * infectionClosureMultiplier *
                embeddedMultiplier * immunosuppressionMultiplier * contaminationExposure * seconds;
            if ((wound.TreatmentFlags & NCWoundTreatmentFlags.Debrided) != 0)
                infection -= progression.DebridementInfectionRecoveryPerSecond * seconds;
            infection -= MathF.Max(0f, pharmacology.AntibioticPower) *
                         progression.AntibioticRecoveryPerPowerPerSecond * seconds;
            changed |= SetIfDifferent(ref wound.Infection, Math.Clamp(infection, 0f, 1f));
        }

        var infectionStage = ResolveInfectionStage(wound, progression.InfectionStageThresholds);
        if (infectionStage != wound.InfectionStage)
        {
            wound.InfectionStage = infectionStage;
            changed = true;
        }

        if (seconds > 0f && wound.InfectionStage >= progression.NecrosisStartsAtInfectionStage)
        {
            changed |= SetIfDifferent(
                ref wound.Necrosis,
                Math.Clamp(
                    wound.Necrosis + progression.InfectionNecrosisPerSecond * seconds,
                    0f,
                    1f));
        }

        if (wound.Necrosis >= progression.NecroticThreshold)
        {
            if (wound.Stage != NCWoundStage.Necrotic)
            {
                wound.Stage = NCWoundStage.Necrotic;
                changed = true;
            }
            wound.ModifiedAt = changed ? now : wound.ModifiedAt;
            return new WoundAdvanceOutcome(changed, false, false, false);
        }

        if (movementStress > 0f && wound.ClosureState is not
            (NCWoundClosureState.Open or NCWoundClosureState.Healed))
        {
            wound.ReopenStress += movementStress * woundPrototype.ReopenSusceptibility;
            changed = true;
        }
        else if (seconds > 0f && wound.ReopenStress > 0f)
        {
            changed |= SetIfDifferent(
                ref wound.ReopenStress,
                MathF.Max(0f, wound.ReopenStress - progression.ReopenStressDecayPerSecond * seconds));
        }

        if (wound.ClosureState is not (NCWoundClosureState.Open or NCWoundClosureState.Healed) &&
            wound.ReopenStress >= progression.ReopenStressThresholds[wound.Severity])
        {
            wound.ClosureState = NCWoundClosureState.Open;
            wound.Stage = NCWoundStage.Reopened;
            wound.HealingProgress *= progression.ReopenHealingRetention;
            wound.ReopenStress = 0f;
            wound.Contamination = Math.Clamp(
                wound.Contamination + progression.ReopenContamination,
                0f,
                1f);
            wound.ScarRisk = Math.Clamp(wound.ScarRisk + progression.ReopenScarRisk, 0f, 1f);
            wound.ExternalBleedRate = MathF.Max(
                wound.ExternalBleedRate,
                wound.InitialExternalBleedRate * progression.ReopenBleedFraction);
            wound.InternalBleedRate = MathF.Max(
                wound.InternalBleedRate,
                wound.InitialInternalBleedRate * progression.ReopenBleedFraction);
            wound.Pain = MathF.Max(
                wound.Pain,
                wound.InitialPain * (1f - wound.HealingProgress * progression.HealingPainReductionFraction));
            wound.TreatmentFlags &= ~NCWoundTreatmentFlags.Clamped;
            if (treatment != null && treatment.WoundTreatments.TryGetValue(wound.WoundId, out var treated))
                treated.AppliedFlags = wound.TreatmentFlags;
            changed = true;
            sourceChanged = true;
            reopened = true;
        }

        if (wound.InfectionStage >= progression.HealingBlockedAtInfectionStage ||
            IsTourniquetActive(wound, treatment, treatmentProfile))
        {
            wound.Stage = wound.InfectionStage >= NCInfectionStage.Localized
                ? NCWoundStage.Infected
                : wound.Stage;
            wound.ModifiedAt = changed ? now : wound.ModifiedAt;
            return new WoundAdvanceOutcome(changed, sourceChanged, reopened, false);
        }

        var canNaturallyClose = wound.Severity <= progression.NaturalClosureMaximumSeverity;
        var canHeal = wound.ClosureState != NCWoundClosureState.Open || canNaturallyClose;
        var perfusion = TryComp<NCCirculationComponent>(body, out var circulation)
            ? Math.Clamp(circulation.Perfusion, 0f, 1f)
            : 0f;
        if (seconds > 0f && canHeal && perfusion >= progression.MinimumHealingPerfusion &&
            progression.HealingDurationBySeverity.TryGetValue(wound.Severity, out var duration) &&
            progression.ClosureHealingMultipliers.TryGetValue(wound.ClosureState, out var closureMultiplier))
        {
            var previousProgress = wound.HealingProgress;
            var durationSeconds = Math.Max(0.001, duration.TotalSeconds);
            wound.HealingProgress = Math.Clamp(
                wound.HealingProgress +
                (float) (seconds / durationSeconds) * closureMultiplier *
                woundPrototype.NaturalHealingMultiplier * perfusion,
                0f,
                1f);
            changed |= MathF.Abs(wound.HealingProgress - previousProgress) > FloatTolerance;

            if (wound.ClosureState == NCWoundClosureState.Open &&
                wound.HealingProgress >= progression.NaturalClosureProgress)
            {
                wound.ClosureState = NCWoundClosureState.TemporarilyClosed;
                wound.Stage = NCWoundStage.Healing;
                changed = true;
            }
            else if (wound.ClosureState != NCWoundClosureState.Open && wound.HealingProgress > 0f)
            {
                wound.Stage = NCWoundStage.Healing;
            }

            var bleedMultiplier = Math.Clamp(
                1f - wound.HealingProgress * progression.HealingBleedReductionFraction,
                0f,
                1f);
            var externalRate = wound.InitialExternalBleedRate * bleedMultiplier;
            var internalRate = wound.InitialInternalBleedRate * bleedMultiplier;
            sourceChanged |= MathF.Abs(wound.ExternalBleedRate - externalRate) > FloatTolerance ||
                             MathF.Abs(wound.InternalBleedRate - internalRate) > FloatTolerance;
            wound.ExternalBleedRate = externalRate;
            wound.InternalBleedRate = internalRate;
            wound.Pain = MathF.Max(
                0f,
                wound.InitialPain *
                (1f - wound.HealingProgress * progression.HealingPainReductionFraction));

            if (wound.HealingProgress > wound.PeakHealingProgress)
            {
                ApplyNaturalRecovery(
                    body,
                    wound,
                    woundPrototype,
                    wound.HealingProgress - wound.PeakHealingProgress);
                wound.PeakHealingProgress = wound.HealingProgress;
            }
        }

        if (wound.HealingProgress >= 1f && wound.ClosureState != NCWoundClosureState.Open)
        {
            wound.ClosureState = NCWoundClosureState.Healed;
            wound.Stage = NCWoundStage.Scarred;
            wound.ExternalBleedRate = 0f;
            wound.InternalBleedRate = 0f;
            wound.Pain = MathF.Max(
                0f,
                wound.InitialPain * (1f - progression.HealingPainReductionFraction));
            wound.ModifiedAt = now;
            return new WoundAdvanceOutcome(true, true, reopened, true);
        }

        if (wound.InfectionStage >= NCInfectionStage.Localized)
            wound.Stage = NCWoundStage.Infected;
        else if (wound.ClosureState != NCWoundClosureState.Open && wound.HealingProgress <= 0f)
            wound.Stage = NCWoundStage.Closed;
        else if (wound.Stage is not (NCWoundStage.Reopened or NCWoundStage.Healing) &&
                 wound.ClosureState == NCWoundClosureState.Open)
            wound.Stage = wound.TreatmentFlags == NCWoundTreatmentFlags.None
                ? NCWoundStage.Open
                : NCWoundStage.Treated;

        wound.ModifiedAt = changed ? now : wound.ModifiedAt;
        return new WoundAdvanceOutcome(changed, sourceChanged, reopened, false);
    }

    private void ApplyNaturalRecovery(
        EntityUid body,
        NCWoundRecord wound,
        NCWoundPrototype prototype,
        float progressDelta)
    {
        if (progressDelta <= 0f || prototype.NaturalIntegrityRecoveryFraction <= 0f ||
            !_anatomy.TryGetOrgan(body, wound.RegionCategory, out var region))
        {
            return;
        }

        var recoverableDamage = wound.IntegrityDamage.Float() *
                                MathF.Max(0f, prototype.RegionIntegrityDamageMultiplier) *
                                prototype.NaturalIntegrityRecoveryFraction * progressDelta;
        if (TryComp<NCOrganIntegrityComponent>(region, out var integrity) && integrity.Recoverable &&
            integrity.Material == NCAnatomicalMaterialKind.Organic)
        {
            integrity.CurrentIntegrity = Math.Clamp(
                integrity.CurrentIntegrity + recoverableDamage,
                0f,
                MathF.Max(0f, integrity.MaxIntegrity));
            integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
                ? integrity.CurrentIntegrity / integrity.MaxIntegrity
                : 0f;
        }

        if (TryComp<NCOrganFunctionComponent>(region, out var function) && function.Recoverable)
        {
            var functionRecovery = wound.IntegrityDamage.Float() * prototype.FunctionLossPerDamage *
                                   prototype.NaturalIntegrityRecoveryFraction * progressDelta;
            function.CurrentCapacity = Math.Clamp(
                function.CurrentCapacity + functionRecovery,
                0f,
                MathF.Max(0f, function.BaseCapacity));
        }
    }

    private static void AddScar(
        NCWoundContainerComponent container,
        NCWoundRecord wound,
        NCWoundProgressionProfilePrototype progression,
        TimeSpan now)
    {
        if (wound.ScarRisk < progression.ScarCreationThreshold &&
            wound.Severity < progression.GuaranteedScarFromSeverity)
        {
            return;
        }

        var scar = new NCScarRecord
        {
            ScarId = wound.WoundId,
            SourceWoundId = wound.WoundId,
            TargetZone = wound.TargetZone,
            RegionCategory = wound.RegionCategory,
            WoundType = wound.WoundType,
            OriginalSeverity = wound.Severity,
            Area = MathF.Max(0f, wound.Area),
            Disfigurement = Math.Clamp(wound.ScarRisk, 0f, 1f),
            Contracture = progression.ScarContractureBySeverity[wound.Severity],
            VisualX = wound.VisualX,
            VisualY = wound.VisualY,
            VisualDirection = wound.VisualDirection,
            CreatedAt = now,
        };

        if (container.Scars.Any(existing => existing.SourceWoundId == wound.WoundId))
            return;
        if (container.Scars.Count < progression.MaximumScars)
        {
            container.Scars.Add(scar);
            return;
        }

        // At the hard budget, aggregate a compatible visible scar; otherwise retain the most consequential
        // records. Ordering by stable Guid makes the fallback deterministic across save/load boundaries.
        NCScarRecord? compatible = null;
        foreach (var existing in container.Scars)
        {
            if (existing.TargetZone != scar.TargetZone || existing.WoundType != scar.WoundType ||
                compatible != null && existing.ScarId.CompareTo(compatible.ScarId) >= 0)
            {
                continue;
            }
            compatible = existing;
        }

        if (compatible != null)
        {
            compatible.Area += scar.Area;
            compatible.Disfigurement = MathF.Max(compatible.Disfigurement, scar.Disfigurement);
            compatible.Contracture = MathF.Max(compatible.Contracture, scar.Contracture);
            return;
        }

        var leastIndex = 0;
        for (var i = 1; i < container.Scars.Count; i++)
        {
            var candidate = container.Scars[i];
            var least = container.Scars[leastIndex];
            if (candidate.Disfigurement < least.Disfigurement ||
                candidate.Disfigurement == least.Disfigurement && candidate.ScarId.CompareTo(least.ScarId) < 0)
            {
                leastIndex = i;
            }
        }
        if (scar.Disfigurement > container.Scars[leastIndex].Disfigurement)
            container.Scars[leastIndex] = scar;
    }

    private static NCInfectionStage ResolveInfectionStage(
        NCWoundRecord wound,
        IReadOnlyList<float> thresholds)
    {
        if (wound.Infection >= thresholds[4])
            return NCInfectionStage.Sepsis;
        if (wound.Infection >= thresholds[3])
            return NCInfectionStage.Systemic;
        if (wound.Infection >= thresholds[2])
            return NCInfectionStage.Organ;
        if (wound.Infection >= thresholds[1])
            return NCInfectionStage.Deep;
        if (wound.Infection >= thresholds[0])
            return NCInfectionStage.Localized;
        return wound.Contamination > 0f ? NCInfectionStage.Contaminated : NCInfectionStage.None;
    }

    private static float GetBandageSterility(Guid woundId, NCTreatmentStateComponent? treatment)
    {
        if (treatment == null || !treatment.WoundTreatments.TryGetValue(woundId, out var record) ||
            (record.AppliedFlags & NCWoundTreatmentFlags.Bandaged) == 0 ||
            record.BandageAbsorptionRemaining <= 0f)
        {
            return 0f;
        }
        return Math.Clamp(record.BandageSterility, 0f, 1f);
    }

    private static bool IsTourniquetActive(
        NCWoundRecord wound,
        NCTreatmentStateComponent? treatment,
        NCTreatmentProfilePrototype? profile)
    {
        return treatment != null && profile != null &&
               profile.TourniquetZoneRoots.TryGetValue(wound.TargetZone, out var root) &&
               treatment.Tourniquets.TryGetValue(root, out var tourniquet) && tourniquet.Active;
    }

    private static void InitializeLongitudinalBaselines(NCWoundRecord wound)
    {
        if (wound.InitialExternalBleedRate <= 0f && wound.ExternalBleedRate > 0f)
            wound.InitialExternalBleedRate = wound.ExternalBleedRate;
        if (wound.InitialInternalBleedRate <= 0f && wound.InternalBleedRate > 0f)
            wound.InitialInternalBleedRate = wound.InternalBleedRate;
        if (wound.InitialPain <= 0f && wound.Pain > 0f)
            wound.InitialPain = wound.Pain;
        wound.HealingProgress = Math.Clamp(wound.HealingProgress, 0f, 1f);
        wound.PeakHealingProgress = Math.Clamp(
            MathF.Max(wound.PeakHealingProgress, wound.HealingProgress),
            0f,
            1f);
        wound.Necrosis = Math.Clamp(wound.Necrosis, 0f, 1f);
    }

    private bool TryGetProfiles(
        NCWoundContainerComponent container,
        out NCWoundSystemProfilePrototype woundSystem,
        out NCWoundProgressionProfilePrototype progression)
    {
        woundSystem = default!;
        progression = default!;
        if (string.IsNullOrWhiteSpace(container.Profile.Id) ||
            !_prototypes.TryIndex(container.Profile, out NCWoundSystemProfilePrototype? resolvedWoundSystem) ||
            !_prototypes.TryIndex(
                resolvedWoundSystem.ProgressionProfile,
                out NCWoundProgressionProfilePrototype? resolvedProgression) ||
            !IsValid(resolvedProgression))
        {
            return false;
        }

        woundSystem = resolvedWoundSystem;
        progression = resolvedProgression;
        return true;
    }

    private bool IsValid(NCWoundProgressionProfilePrototype profile)
    {
        if (profile.InfectionStageThresholds.Count != 5 ||
            profile.FreshToOpenDelay < TimeSpan.Zero ||
            !IsFiniteRange(profile.NaturalClosureProgress, FloatTolerance, 1f) ||
            !IsFiniteRange(profile.InfectionContaminationThreshold, 0f, 1f - FloatTolerance) ||
            !IsFiniteRange(profile.NecroticThreshold, FloatTolerance, 1f) ||
            !IsFiniteRange(profile.HealingBleedReductionFraction, 0f, 1f) ||
            !IsFiniteRange(profile.HealingPainReductionFraction, 0f, 1f) ||
            !IsFiniteRange(profile.MinimumHealingPerfusion, 0f, 1f) ||
            !IsFiniteRange(profile.ReopenHealingRetention, 0f, 1f) ||
            !IsFiniteRange(profile.ReopenBleedFraction, 0f, 1f) ||
            !float.IsFinite(profile.MaximumPendingMovementDistance) ||
            profile.MaximumPendingMovementDistance <= 0f ||
            profile.PersistenceCheckpointInterval <= TimeSpan.Zero ||
            !IsFiniteRange(profile.ScarCreationThreshold, 0f, 1f) ||
            profile.MaximumScars <= 0 ||
            !Enum.IsDefined(profile.NaturalClosureMaximumSeverity) ||
            !Enum.IsDefined(profile.HealingBlockedAtInfectionStage) ||
            !Enum.IsDefined(profile.NecrosisStartsAtInfectionStage) ||
            !Enum.IsDefined(profile.GuaranteedScarFromSeverity) ||
            !FiniteNonNegative(
                profile.OpenContaminationPerSecond,
                profile.SterileContaminationDecayPerSecond,
                profile.InfectionGrowthPerSecond,
                profile.DebridementInfectionRecoveryPerSecond,
                profile.AntibioticRecoveryPerPowerPerSecond,
                profile.ImmunosuppressionInfectionMultiplier,
                profile.EmbeddedObjectInfectionMultiplier,
                profile.ClosedInfectionMultiplier,
                profile.InfectionNecrosisPerSecond,
                profile.ReopenContamination,
                profile.ReopenScarRisk,
                profile.ReopenStressDecayPerSecond,
                profile.MovementStressPerTile,
                profile.IschemiaIntegrityDamagePerSecond,
                profile.CriticalIschemiaIntegrityDamagePerSecond,
                profile.IschemiaNerveDamagePerSecond,
                profile.CriticalIschemiaNerveDamagePerSecond,
                profile.IschemiaNecrosisPerSecond,
                profile.CriticalIschemiaNecrosisPerSecond,
                profile.SepsisAntibioticRecoveryPerPowerPerSecond) ||
            profile.SepsisExposurePerSecondByStage.Count != 2 ||
            !TryFiniteNonNegative(profile.SepsisExposurePerSecondByStage, NCInfectionStage.Systemic) ||
            !TryFiniteNonNegative(profile.SepsisExposurePerSecondByStage, NCInfectionStage.Sepsis) ||
            !_prototypes.TryIndex(
                profile.SepsisSystemicProfile,
                out NCSystemicDamageProfilePrototype? sepsisProfile) ||
            sepsisProfile.Channel != NCSystemicChannel.Sepsis ||
            !IsStrictlyAscendingFraction(profile.InfectionStageThresholds))
        {
            return false;
        }

        foreach (var severity in Enum.GetValues<NCWoundSeverity>())
        {
            if (!profile.HealingDurationBySeverity.TryGetValue(severity, out var duration) ||
                duration <= TimeSpan.Zero ||
                !TryFiniteNonNegative(profile.ReopenStressThresholds, severity) ||
                !TryFiniteNonNegative(profile.ImpactReopenStressBySeverity, severity) ||
                !TryFiniteNonNegative(profile.InfectionSeverityMultipliers, severity) ||
                !TryFiniteRange(profile.ScarContractureBySeverity, severity, 0f, 1f))
            {
                return false;
            }
        }
        foreach (var closure in Enum.GetValues<NCWoundClosureState>())
        {
            if (!TryFiniteNonNegative(profile.ClosureHealingMultipliers, closure))
                return false;
        }
        return true;
    }

    private static bool IsValid(NCWoundPrototype prototype)
    {
        return FiniteNonNegative(
                   prototype.NaturalHealingMultiplier,
                   prototype.InfectionSusceptibility,
                   prototype.ReopenSusceptibility,
                   prototype.NaturalIntegrityRecoveryFraction) &&
               prototype.NaturalIntegrityRecoveryFraction <= 1f;
    }

    private static bool FiniteNonNegative(params float[] values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value) || value < 0f)
                return false;
        }
        return true;
    }

    private static bool TryFiniteNonNegative<T>(IReadOnlyDictionary<T, float> values, T key)
        where T : notnull
    {
        return values.TryGetValue(key, out var value) && float.IsFinite(value) && value >= 0f;
    }

    private static bool TryFiniteRange<T>(
        IReadOnlyDictionary<T, float> values,
        T key,
        float minimum,
        float maximum)
        where T : notnull
    {
        return values.TryGetValue(key, out var value) && IsFiniteRange(value, minimum, maximum);
    }

    private static bool IsFiniteRange(float value, float minimum, float maximum)
    {
        return float.IsFinite(value) && value >= minimum && value <= maximum;
    }

    private static bool IsStrictlyAscendingFraction(IReadOnlyList<float> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (!float.IsFinite(values[i]) || values[i] is <= 0f or > 1f ||
                i > 0 && values[i] <= values[i - 1])
            {
                return false;
            }
        }
        return true;
    }

    private static float OverlapSeconds(
        TimeSpan intervalStart,
        TimeSpan intervalEnd,
        TimeSpan rangeStart,
        TimeSpan rangeEnd)
    {
        var start = intervalStart > rangeStart ? intervalStart : rangeStart;
        var end = intervalEnd < rangeEnd ? intervalEnd : rangeEnd;
        return end > start ? (float) (end - start).TotalSeconds : 0f;
    }

    private static bool SetIfDifferent(ref float field, float value)
    {
        if (MathF.Abs(field - value) <= FloatTolerance)
            return false;
        field = value;
        return true;
    }

    private readonly record struct WoundAdvanceOutcome(
        bool Changed,
        bool SourceChanged,
        bool Reopened,
        bool Resolved);
}

public readonly record struct NCWoundProgressionResult(
    bool Accepted,
    bool Changed,
    int ReopenedWounds,
    int ScarredWounds,
    int SaturatedBandages,
    int IschemicRegions);
