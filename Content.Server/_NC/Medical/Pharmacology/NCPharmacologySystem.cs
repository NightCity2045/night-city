// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Body;
using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Pharmacology;

/// <summary>
/// Converts quantities consumed by upstream metabolism into bounded, deterministic physiological state.
/// It has no update loop: active bodies advance only through the central medical scheduler.
/// </summary>
public sealed partial class NCPharmacologySystem : EntityEffectSystem<NCMedicalBodyComponent, NCApplyMedicalDrug>
{
    private const int AbsoluteDrugCapacityLimit = 64;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPharmacologyComponent, NCMedicalScheduledUpdateEvent>(OnScheduledUpdate);
        SubscribeLocalEvent<NCPharmacologyComponent, NCMedicalNextUpdateEvent>(OnNextUpdateRequested);
    }

    protected override void Effect(Entity<NCMedicalBodyComponent> entity, ref EntityEffectEvent<NCApplyMedicalDrug> args)
    {
        var amount = args.Scale * args.Effect.ConcentrationPerUnit;
        ApplyDose(entity.Owner, args.Effect.Profile, amount);
    }

    private void OnScheduledUpdate(
        Entity<NCPharmacologyComponent> entity,
        ref NCMedicalScheduledUpdateEvent args)
    {
        AdvanceBody(entity.Owner, args.Elapsed, entity.Comp);
    }

    private void OnNextUpdateRequested(
        Entity<NCPharmacologyComponent> entity,
        ref NCMedicalNextUpdateEvent args)
    {
        if (entity.Comp.ActiveDrugs.Count == 0 && entity.Comp.OverdoseBurden <= 0f)
            return;

        if (!_prototypes.TryIndex(entity.Comp.Profile, out NCPharmacologyProfilePrototype? profile) ||
            !ValidateProfile(profile))
        {
            return;
        }

        if (args.Delay == null || profile.ActiveUpdateInterval < args.Delay)
            args.Delay = profile.ActiveUpdateInterval;
    }

    /// <summary>
    /// Applies a measured metabolized quantity. Calling code never selects an outcome; concentration, organ
    /// clearance and interactions fully determine the result.
    /// </summary>
    public NCDrugDoseResult ApplyDose(
        EntityUid body,
        ProtoId<NCDrugProfilePrototype> drugId,
        float concentration)
    {
        if (!float.IsFinite(concentration) || concentration <= 0f ||
            !HasComp<NCMedicalBodyComponent>(body) ||
            !_prototypes.TryIndex(drugId, out NCDrugProfilePrototype? drug) ||
            !ValidateDrug(drug) ||
            !TryEnsureInitialized(body, out var state, out var profile))
        {
            return default;
        }

        if (!state.ActiveDrugs.TryGetValue(drugId, out var exposure))
        {
            if (state.ActiveDrugs.Count >= profile.ActiveDrugCapacity)
                return new NCDrugDoseResult(false, drugId, 0f, 0f, true);

            exposure = new NCActiveDrugExposure
            {
                Profile = drugId,
            };
            state.ActiveDrugs.Add(drugId, exposure);
        }

        var previous = exposure.Concentration;
        exposure.Concentration = Math.Clamp(previous + concentration, 0f, drug.MaximumConcentration);
        exposure.CumulativeDose = Math.Clamp(
            exposure.CumulativeDose + concentration,
            0f,
            profile.MaximumRecordedCumulativeDose);
        exposure.LastAdministration = _timing.CurTime;
        RecomputeModifiers(state, profile);
        MarkChanged(body, drugId, previous, exposure.Concentration, state.OverdoseBurden);
        return new NCDrugDoseResult(true, drugId, previous, exposure.Concentration, false);
    }

    /// <summary>
    /// Advances clearance, interactions and overdose consequences for one scheduled body.
    /// </summary>
    public bool AdvanceBody(EntityUid body, TimeSpan elapsed, NCPharmacologyComponent? state = null)
    {
        if (elapsed <= TimeSpan.Zero ||
            !Resolve(body, ref state, false) ||
            !_prototypes.TryIndex(state.Profile, out NCPharmacologyProfilePrototype? profile) ||
            !ValidateProfile(profile))
        {
            return false;
        }

        var seconds = (float) elapsed.TotalSeconds;
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return false;

        var liverFunction = GetOrganFunction(body, NCOrganFunctionType.Metabolism);
        var kidneyFunction = GetOrganFunction(body, NCOrganFunctionType.Filtration);
        var changed = false;
        var overdoseAdded = 0f;

        state.RemovalBuffer.Clear();
        BuildEvaluationOrder(state);
        foreach (var drugId in state.EvaluationOrder)
        {
            var exposure = state.ActiveDrugs[drugId];
            if (!_prototypes.TryIndex(drugId, out NCDrugProfilePrototype? drug) || !ValidateDrug(drug))
            {
                state.RemovalBuffer.Add(drugId);
                changed = true;
                continue;
            }

            var previous = exposure.Concentration;
            var clearanceMultiplier = Math.Clamp(
                drug.BaselineClearanceFraction +
                drug.LiverClearanceFraction * liverFunction +
                drug.KidneyClearanceFraction * kidneyFunction,
                0f,
                1f);
            exposure.Concentration = MathF.Max(
                0f,
                previous - drug.ClearancePerSecond * clearanceMultiplier * seconds);

            var averageConcentration = (previous + exposure.Concentration) * 0.5f;
            var overdoseFraction = ResolveOverdoseFraction(drug, averageConcentration);
            overdoseAdded += overdoseFraction *
                             drug.OverdoseBurdenPerSecondAtMaximum *
                             ResolveInteractionToxicity(state, profile, drugId, drug) *
                             seconds;

            if (exposure.Concentration <= drug.RemovalConcentration)
                state.RemovalBuffer.Add(drugId);

            changed |= !MathHelper.CloseTo(previous, exposure.Concentration);
        }

        foreach (var drugId in state.RemovalBuffer)
            state.ActiveDrugs.Remove(drugId);
        state.RemovalBuffer.Clear();

        var previousOverdose = state.OverdoseBurden;
        state.OverdoseBurden = Math.Clamp(
            MathF.Max(0f, previousOverdose - profile.OverdoseRecoveryPerSecond * seconds) + overdoseAdded,
            0f,
            profile.MaximumOverdoseBurden);
        changed |= !MathHelper.CloseTo(previousOverdose, state.OverdoseBurden);

        RecomputeModifiers(state, profile);
        if (state.CachedModifiers.ToxinRecoveryPerSecond > 0f)
        {
            _systemic.Apply(
                body,
                profile.ToxinSystemicProfile,
                -state.CachedModifiers.ToxinRecoveryPerSecond * seconds);
        }

        if (state.CachedModifiers.OverdoseFraction > profile.OrganDamageThreshold)
        {
            var damagingFraction = Math.Clamp(
                (state.CachedModifiers.OverdoseFraction - profile.OrganDamageThreshold) /
                MathF.Max(float.Epsilon, 1f - profile.OrganDamageThreshold),
                0f,
                1f);
            changed |= DamageOrgans(
                body,
                damagingFraction * profile.LiverDamagePerSecondAtMaximum * seconds,
                damagingFraction * profile.KidneyDamagePerSecondAtMaximum * seconds);
        }

        state.LastEvaluation += elapsed;
        if (changed)
        {
            _medicalBodies.TryMarkDirty(
                body,
                NCMedicalDirtyDomains.Pain |
                NCMedicalDirtyDomains.Vascular |
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence);
        }

        return changed;
    }

    public NCPharmacologyModifiers GetModifiers(EntityUid body)
    {
        if (!TryComp<NCPharmacologyComponent>(body, out var state) ||
            !_prototypes.TryIndex(state.Profile, out NCPharmacologyProfilePrototype? profile) ||
            !ValidateProfile(profile))
        {
            return NCPharmacologyModifiers.Neutral;
        }

        RecomputeModifiers(state, profile);
        return state.CachedModifiers;
    }

    /// <summary>
    /// Captures bounded semantic exposure state. Absence is a valid null snapshot; malformed live state is
    /// rejected so persistence cannot silently normalize a corrupt pharmacology graph.
    /// </summary>
    public bool TryCapturePersistentState(
        EntityUid body,
        TimeSpan timestamp,
        out NCPharmacologyStateSnapshot? snapshot)
    {
        snapshot = null;
        if (!HasComp<NCMedicalBodyComponent>(body) || timestamp < TimeSpan.Zero)
            return false;
        if (!TryComp<NCPharmacologyComponent>(body, out var state))
            return true;
        if (!_prototypes.TryIndex(state.Profile, out NCPharmacologyProfilePrototype? profile) ||
            !ValidateProfile(profile) ||
            state.ActiveDrugs.Count > profile.ActiveDrugCapacity ||
            !IsFiniteRange(state.OverdoseBurden, 0f, profile.MaximumOverdoseBurden))
        {
            return false;
        }

        var captured = new NCPharmacologyStateSnapshot
        {
            Profile = state.Profile,
            Initialized = state.Initialized,
            OverdoseBurden = state.OverdoseBurden,
        };
        foreach (var (drugId, exposure) in state.ActiveDrugs.OrderBy(
                     entry => entry.Key.Id,
                     StringComparer.Ordinal))
        {
            if (exposure.Profile != drugId ||
                !_prototypes.TryIndex(drugId, out NCDrugProfilePrototype? drug) ||
                !ValidateDrug(drug) ||
                !IsFiniteRange(exposure.Concentration, 0f, drug.MaximumConcentration) ||
                !IsFiniteRange(exposure.CumulativeDose, 0f, profile.MaximumRecordedCumulativeDose) ||
                exposure.LastAdministration < TimeSpan.Zero ||
                exposure.LastAdministration > timestamp)
            {
                return false;
            }

            captured.ActiveDrugs.Add(new NCActiveDrugExposureSnapshot
            {
                Profile = drugId,
                Concentration = exposure.Concentration,
                CumulativeDose = exposure.CumulativeDose,
                TimeSinceAdministration = Min(
                    timestamp - exposure.LastAdministration,
                    profile.MaximumRecordedExposureAge),
            });
        }

        snapshot = captured;
        return true;
    }

    /// <summary>
    /// Validates and restores a semantic snapshot atomically. Runtime buffers and action correlations are
    /// rebuilt empty, and exposure ages are rebased to the new monotonic server clock.
    /// </summary>
    public bool TryRestorePersistentState(
        EntityUid body,
        NCPharmacologyStateSnapshot? snapshot,
        TimeSpan timestamp)
    {
        if (!HasComp<NCMedicalBodyComponent>(body))
            return false;
        if (snapshot == null)
        {
            RemComp<NCPharmacologyComponent>(body);
            return true;
        }
        if (!ValidatePersistentState(snapshot))
            return false;

        var state = EnsureComp<NCPharmacologyComponent>(body);
        state.Profile = snapshot.Profile;
        state.Initialized = snapshot.Initialized;
        state.OverdoseBurden = snapshot.OverdoseBurden;
        state.ActiveDrugs.Clear();
        foreach (var exposure in snapshot.ActiveDrugs)
        {
            state.ActiveDrugs.Add(exposure.Profile, new NCActiveDrugExposure
            {
                Profile = exposure.Profile,
                Concentration = exposure.Concentration,
                CumulativeDose = exposure.CumulativeDose,
                LastAdministration = timestamp >= exposure.TimeSinceAdministration
                    ? timestamp - exposure.TimeSinceAdministration
                    : TimeSpan.Zero,
            });
        }
        state.RemovalBuffer.Clear();
        state.EvaluationOrder.Clear();
        state.LastEvaluation = timestamp;
        var profile = _prototypes.Index(state.Profile);
        RecomputeModifiers(state, profile);
        return true;
    }

    public bool ValidatePersistentState(NCPharmacologyStateSnapshot snapshot)
    {
        if (snapshot.ActiveDrugs == null ||
            !_prototypes.TryIndex(snapshot.Profile, out NCPharmacologyProfilePrototype? profile) ||
            !ValidateProfile(profile) ||
            snapshot.ActiveDrugs.Count > profile.ActiveDrugCapacity ||
            !IsFiniteRange(snapshot.OverdoseBurden, 0f, profile.MaximumOverdoseBurden) ||
            (!snapshot.Initialized &&
             (snapshot.ActiveDrugs.Count > 0 || snapshot.OverdoseBurden > 0f)))
        {
            return false;
        }

        var seen = new HashSet<ProtoId<NCDrugProfilePrototype>>();
        foreach (var exposure in snapshot.ActiveDrugs)
        {
            if (exposure == null ||
                !seen.Add(exposure.Profile) ||
                !_prototypes.TryIndex(exposure.Profile, out NCDrugProfilePrototype? drug) ||
                !ValidateDrug(drug) ||
                !IsFiniteRange(exposure.Concentration, 0f, drug.MaximumConcentration) ||
                !IsFiniteRange(exposure.CumulativeDose, 0f, profile.MaximumRecordedCumulativeDose) ||
                exposure.TimeSinceAdministration < TimeSpan.Zero ||
                exposure.TimeSinceAdministration > profile.MaximumRecordedExposureAge)
            {
                return false;
            }
        }
        return true;
    }

    public bool ResetBody(EntityUid body)
    {
        if (!TryComp<NCPharmacologyComponent>(body, out var state))
            return HasComp<NCMedicalBodyComponent>(body);

        state.ActiveDrugs.Clear();
        state.RemovalBuffer.Clear();
        state.EvaluationOrder.Clear();
        state.OverdoseBurden = 0f;
        state.LastEvaluation = _timing.CurTime;
        state.CachedModifiers = NCPharmacologyModifiers.Neutral;
        return true;
    }

    private bool TryEnsureInitialized(
        EntityUid body,
        out NCPharmacologyComponent state,
        out NCPharmacologyProfilePrototype profile)
    {
        state = EnsureComp<NCPharmacologyComponent>(body);
        if (state.Initialized &&
            _prototypes.TryIndex(state.Profile, out NCPharmacologyProfilePrototype? resolvedProfile) &&
            ValidateProfile(resolvedProfile))
        {
            profile = resolvedProfile;
            return true;
        }

        var defaults = _prototypes.EnumeratePrototypes<NCPharmacologyProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();
        if (defaults.Count != 1 || !ValidateProfile(defaults[0]))
        {
            profile = default!;
            return false;
        }

        profile = defaults[0];
        state.Profile = profile.ID;
        state.Initialized = true;
        state.LastEvaluation = _timing.CurTime;
        state.CachedModifiers = NCPharmacologyModifiers.Neutral;
        return true;
    }

    private void RecomputeModifiers(
        NCPharmacologyComponent state,
        NCPharmacologyProfilePrototype profile)
    {
        var analgesia = 0f;
        var sedation = 0f;
        var anesthesia = 0f;
        var arousal = 0f;
        var cardiac = 1f;
        var respiratory = 1f;
        var oxygenation = 1f;
        var oxygenCarrier = 0f;
        var clotting = 1f;
        var rhythmStability = 0f;
        var rhythmInstability = 0f;
        var motorControl = 1f;
        var antibiotic = 0f;
        var immunosuppression = 0f;
        var toxinRecovery = 0f;

        BuildEvaluationOrder(state);
        foreach (var drugId in state.EvaluationOrder)
        {
            var exposure = state.ActiveDrugs[drugId];
            if (!_prototypes.TryIndex(drugId, out NCDrugProfilePrototype? drug) || !ValidateDrug(drug))
                continue;

            var fraction = ResolveTherapeuticFraction(drug, exposure.Concentration) *
                           ResolveInteractionTherapeutics(state, profile, drugId, drug);
            if (fraction <= 0f)
                continue;

            analgesia += drug.AnalgesiaAtTherapeutic * fraction;
            sedation += drug.SedationAtTherapeutic * fraction;
            anesthesia += drug.AnesthesiaAtTherapeutic * fraction;
            arousal += drug.ArousalAtTherapeutic * fraction;
            cardiac += (drug.CardiacOutputMultiplierAtTherapeutic - 1f) * fraction;
            respiratory += (drug.RespiratoryTransferMultiplierAtTherapeutic - 1f) * fraction;
            oxygenation += (drug.OxygenationMultiplierAtTherapeutic - 1f) * fraction;
            oxygenCarrier += drug.OxygenCarrierContributionAtTherapeutic * fraction;
            clotting *= 1f + (drug.BleedingMultiplierAtTherapeutic - 1f) * fraction;
            rhythmStability += drug.RhythmStabilityAtTherapeutic * fraction;
            rhythmInstability += drug.RhythmInstabilityAtTherapeutic * fraction;
            motorControl *= 1f + (drug.MotorControlMultiplierAtTherapeutic - 1f) * fraction;
            antibiotic += drug.AntibioticPowerAtTherapeutic * fraction;
            immunosuppression += drug.ImmunosuppressionAtTherapeutic * fraction;
            toxinRecovery += drug.ToxinRecoveryPerSecondAtTherapeutic * fraction;
        }

        var overdoseFraction = profile.MaximumOverdoseBurden > 0f
            ? Math.Clamp(state.OverdoseBurden / profile.MaximumOverdoseBurden, 0f, 1f)
            : 0f;
        var arrhythmiaFraction = profile.OverdoseArrhythmiaThreshold >= 1f
            ? 0f
            : Math.Clamp(
                (overdoseFraction - profile.OverdoseArrhythmiaThreshold) /
                (1f - profile.OverdoseArrhythmiaThreshold),
                0f,
                1f);
        state.CachedModifiers = new NCPharmacologyModifiers(
            Math.Clamp(analgesia, 0f, 1f),
            Math.Clamp(sedation, 0f, 1f),
            Math.Clamp(anesthesia, 0f, 1f),
            Math.Clamp(arousal, 0f, 1f),
            Math.Clamp(
                cardiac * (1f - profile.OverdoseCardiacPenaltyAtMaximum * overdoseFraction),
                profile.MinimumPhysiologyMultiplier,
                profile.MaximumPhysiologyMultiplier),
            Math.Clamp(
                respiratory * (1f - profile.OverdoseRespiratoryPenaltyAtMaximum * overdoseFraction),
                profile.MinimumPhysiologyMultiplier,
                profile.MaximumPhysiologyMultiplier),
            Math.Clamp(
                oxygenation * (1f - profile.OverdoseRespiratoryPenaltyAtMaximum * overdoseFraction),
                profile.MinimumPhysiologyMultiplier,
                profile.MaximumPhysiologyMultiplier),
            Math.Clamp(oxygenCarrier, 0f, 1f),
            Math.Clamp(clotting, profile.MinimumClottingMultiplier, profile.MaximumClottingMultiplier),
            Math.Clamp(rhythmStability, 0f, 1f),
            Math.Clamp(
                rhythmInstability + profile.OverdoseRhythmInstabilityAtMaximum * arrhythmiaFraction,
                0f,
                1f),
            Math.Clamp(
                motorControl * (1f - profile.OverdoseMotorControlPenaltyAtMaximum * overdoseFraction),
                0f,
                1f),
            Math.Clamp(antibiotic, 0f, 1f),
            Math.Clamp(immunosuppression, 0f, 1f),
            Math.Clamp(toxinRecovery, 0f, profile.MaximumToxinRecoveryPerSecond),
            Math.Clamp(profile.OverdoseShockAtMaximum * overdoseFraction, 0f, 1f),
            Math.Clamp(1f - profile.OverdoseBrainPenaltyAtMaximum * overdoseFraction, 0f, 1f),
            overdoseFraction);
    }

    private void BuildEvaluationOrder(NCPharmacologyComponent state)
    {
        state.EvaluationOrder.Clear();
        foreach (var drugId in state.ActiveDrugs.Keys)
            state.EvaluationOrder.Add(drugId);
        state.EvaluationOrder.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
    }

    private static float ResolveTherapeuticFraction(NCDrugProfilePrototype drug, float concentration)
    {
        if (concentration < drug.MinimumEffectiveConcentration)
            return 0f;
        return Math.Clamp(concentration / drug.TherapeuticConcentration, 0f, 1f);
    }

    private static float ResolveOverdoseFraction(NCDrugProfilePrototype drug, float concentration)
    {
        if (concentration <= drug.ToxicConcentration)
            return 0f;
        return Math.Clamp(
            (concentration - drug.ToxicConcentration) /
            (drug.MaximumConcentration - drug.ToxicConcentration),
            0f,
            1f);
    }

    private float ResolveInteractionTherapeutics(
        NCPharmacologyComponent state,
        NCPharmacologyProfilePrototype profile,
        ProtoId<NCDrugProfilePrototype> drugId,
        NCDrugProfilePrototype drug)
    {
        var multiplier = 1f;
        foreach (var other in state.EvaluationOrder)
        {
            if (other == drugId ||
                !drug.Interactions.TryGetValue(other, out var rule) ||
                !state.ActiveDrugs.TryGetValue(other, out var exposure) ||
                !_prototypes.TryIndex(other, out NCDrugProfilePrototype? otherDrug) ||
                !ValidateDrug(otherDrug))
            {
                continue;
            }

            // Pair effects build continuously from the measured partner concentration. A trace exposure
            // therefore cannot activate the full interaction merely because its dictionary entry exists.
            var partnerFraction = ResolveTherapeuticFraction(otherDrug, exposure.Concentration);
            multiplier *= 1f + (rule.TherapeuticMultiplier - 1f) * partnerFraction;
        }
        return Math.Clamp(multiplier, 0f, profile.MaximumInteractionMultiplier);
    }

    private float ResolveInteractionToxicity(
        NCPharmacologyComponent state,
        NCPharmacologyProfilePrototype profile,
        ProtoId<NCDrugProfilePrototype> drugId,
        NCDrugProfilePrototype drug)
    {
        var multiplier = 1f;
        foreach (var other in state.EvaluationOrder)
        {
            if (other == drugId ||
                !drug.Interactions.TryGetValue(other, out var rule) ||
                !state.ActiveDrugs.TryGetValue(other, out var exposure) ||
                !_prototypes.TryIndex(other, out NCDrugProfilePrototype? otherDrug) ||
                !ValidateDrug(otherDrug))
            {
                continue;
            }

            var partnerFraction = ResolveTherapeuticFraction(otherDrug, exposure.Concentration);
            multiplier *= 1f + (rule.ToxicityMultiplier - 1f) * partnerFraction;
        }
        return Math.Clamp(multiplier, 0f, profile.MaximumInteractionMultiplier);
    }

    private float GetOrganFunction(EntityUid body, NCOrganFunctionType functionType)
    {
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return 0f;

        var found = false;
        var capacity = 1f;
        var organs = bodyComponent.Organs;
        foreach (var organ in organs.ContainedEntities)
        {
            if (!TryComp<NCOrganFunctionComponent>(organ, out var function) ||
                function.FunctionType != functionType)
            {
                continue;
            }

            found = true;
            var fraction = function.BaseCapacity > 0f
                ? Math.Clamp(function.CurrentCapacity / function.BaseCapacity, 0f, 1f)
                : 0f;
            capacity = MathF.Min(capacity, fraction);
        }
        return found ? capacity : 0f;
    }

    private bool DamageOrgans(EntityUid body, float liverDamage, float kidneyDamage)
    {
        if ((liverDamage <= 0f && kidneyDamage <= 0f) ||
            !TryComp<BodyComponent>(body, out var bodyComponent) ||
            bodyComponent.Organs == null)
        {
            return false;
        }

        var changed = false;
        var organs = bodyComponent.Organs;
        foreach (var organ in organs.ContainedEntities)
        {
            if (!TryComp<NCOrganFunctionComponent>(organ, out var function))
                continue;

            var damage = function.FunctionType switch
            {
                NCOrganFunctionType.Metabolism => liverDamage,
                NCOrganFunctionType.Filtration => kidneyDamage,
                _ => 0f,
            };
            if (damage <= 0f)
                continue;

            var previousCapacity = function.CurrentCapacity;
            function.CurrentCapacity = MathF.Max(0f, function.CurrentCapacity - damage);
            if (TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
            {
                integrity.CurrentIntegrity = MathF.Max(
                    0f,
                    integrity.CurrentIntegrity - damage * integrity.MaxIntegrity);
                integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
                    ? Math.Clamp(integrity.CurrentIntegrity / integrity.MaxIntegrity, 0f, 1f)
                    : 0f;
            }

            changed |= !MathHelper.CloseTo(previousCapacity, function.CurrentCapacity);
        }
        return changed;
    }

    private void MarkChanged(
        EntityUid body,
        ProtoId<NCDrugProfilePrototype> drug,
        float previous,
        float current,
        float overdose)
    {
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
        var changed = new NCPharmacologyChangedEvent(drug, previous, current, overdose);
        RaiseLocalEvent(body, ref changed);
    }

    public static bool ValidateProfile(NCPharmacologyProfilePrototype profile)
    {
        return profile.ActiveDrugCapacity is > 0 and <= AbsoluteDrugCapacityLimit &&
               profile.ActiveUpdateInterval > TimeSpan.Zero &&
               IsFinitePositive(profile.MaximumOverdoseBurden) &&
               IsFiniteNonNegative(profile.OverdoseRecoveryPerSecond) &&
               IsFinitePositive(profile.MaximumRecordedCumulativeDose) &&
               profile.MaximumRecordedExposureAge > TimeSpan.Zero &&
               IsFiniteRange(profile.MinimumPhysiologyMultiplier, 0f, 1f) &&
               float.IsFinite(profile.MaximumPhysiologyMultiplier) &&
               profile.MaximumPhysiologyMultiplier >= 1f &&
               IsFiniteRange(profile.MinimumClottingMultiplier, 0f, 1f) &&
               float.IsFinite(profile.MaximumClottingMultiplier) &&
               profile.MaximumClottingMultiplier >= 1f &&
               float.IsFinite(profile.MaximumInteractionMultiplier) &&
               profile.MaximumInteractionMultiplier >= 1f &&
               IsFiniteNonNegative(profile.MaximumToxinRecoveryPerSecond) &&
               IsFiniteRange(profile.OverdoseShockAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseBrainPenaltyAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseCardiacPenaltyAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseRespiratoryPenaltyAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseRhythmInstabilityAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseArrhythmiaThreshold, 0f, 1f) &&
               IsFiniteRange(profile.OverdoseMotorControlPenaltyAtMaximum, 0f, 1f) &&
               IsFiniteRange(profile.OrganDamageThreshold, 0f, 1f) &&
               IsFiniteNonNegative(profile.LiverDamagePerSecondAtMaximum) &&
               IsFiniteNonNegative(profile.KidneyDamagePerSecondAtMaximum) &&
               !string.IsNullOrWhiteSpace(profile.ToxinSystemicProfile.Id);
    }

    public static bool ValidateDrug(NCDrugProfilePrototype drug)
    {
        if (!IsFinitePositive(drug.MaximumConcentration) ||
            !IsFiniteNonNegative(drug.RemovalConcentration) ||
            !IsFiniteNonNegative(drug.MinimumEffectiveConcentration) ||
            !IsFinitePositive(drug.TherapeuticConcentration) ||
            !IsFinitePositive(drug.ToxicConcentration) ||
            drug.RemovalConcentration > drug.MinimumEffectiveConcentration ||
            drug.MinimumEffectiveConcentration > drug.TherapeuticConcentration ||
            drug.TherapeuticConcentration >= drug.ToxicConcentration ||
            drug.ToxicConcentration >= drug.MaximumConcentration ||
            !IsFiniteNonNegative(drug.ClearancePerSecond) ||
            !IsFiniteRange(drug.BaselineClearanceFraction, 0f, 1f) ||
            !IsFiniteRange(drug.LiverClearanceFraction, 0f, 1f) ||
            !IsFiniteRange(drug.KidneyClearanceFraction, 0f, 1f) ||
            drug.BaselineClearanceFraction + drug.LiverClearanceFraction + drug.KidneyClearanceFraction > 1.0001f ||
            !IsFiniteRange(drug.AnalgesiaAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.SedationAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.AnesthesiaAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.ArousalAtTherapeutic, 0f, 1f) ||
            !IsFiniteNonNegative(drug.CardiacOutputMultiplierAtTherapeutic) ||
            !IsFiniteNonNegative(drug.RespiratoryTransferMultiplierAtTherapeutic) ||
            !IsFiniteNonNegative(drug.OxygenationMultiplierAtTherapeutic) ||
            !IsFiniteRange(drug.OxygenCarrierContributionAtTherapeutic, 0f, 1f) ||
            !IsFiniteNonNegative(drug.BleedingMultiplierAtTherapeutic) ||
            !IsFiniteRange(drug.RhythmStabilityAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.RhythmInstabilityAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.MotorControlMultiplierAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.AntibioticPowerAtTherapeutic, 0f, 1f) ||
            !IsFiniteRange(drug.ImmunosuppressionAtTherapeutic, 0f, 1f) ||
            !IsFiniteNonNegative(drug.ToxinRecoveryPerSecondAtTherapeutic) ||
            !IsFiniteNonNegative(drug.OverdoseBurdenPerSecondAtMaximum))
        {
            return false;
        }

        foreach (var rule in drug.Interactions.Values)
        {
            if (!IsFiniteNonNegative(rule.TherapeuticMultiplier) ||
                !IsFiniteNonNegative(rule.ToxicityMultiplier))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
    private static bool IsFiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;
    private static bool IsFiniteRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
