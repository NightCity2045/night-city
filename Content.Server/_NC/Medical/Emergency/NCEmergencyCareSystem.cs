// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.Standing;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Emergency;

/// <summary>
/// Resolves deterministic emergency care from measured patient state and data-defined item capability.
/// It never performs a skill check, random roll, world scan or compatibility-HP mutation.
/// </summary>
public sealed partial class NCEmergencyCareSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _trauma = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCCirculationBloodSystem _blood = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private StandingStateSystem _standing = default!;

    /// <summary>
    /// Applies one short interval of manual compressions. CPR supplies weak mechanical perfusion and slows,
    /// but never halts, hypoxic brain decline. It does not add blood or repair the arrest cause.
    /// </summary>
    public NCEmergencyCareResult PerformCpr(
        EntityUid patient,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return PerformCpr(patient, CreateActionContext(timestamp, actor));
    }

    public NCEmergencyCareResult PerformCpr(
        EntityUid patient,
        in NCEmergencyCareActionContext context)
    {
        if (!TryPrepare(patient, context, out var state, out var profile, out var early))
            return early;

        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        if (!TryComp<NCCirculationComponent>(patient, out var circulation) ||
            !TryComp<NCPhysiologyComponent>(patient, out var physiology))
        {
            return CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr,
                NewResult(context, NCEmergencyCareOutcome.InvalidProfile));
        }

        if (physiology.Irreversible)
        {
            return CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr,
                NewResult(context, NCEmergencyCareOutcome.IrreversiblePatient));
        }

        // SS14's authoritative down state is the measurable supported compression pose. No hidden skill or
        // role modifier can override a standing patient or excessive measured chest trauma.
        if (!_standing.IsDown(patient))
        {
            return CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr,
                NewResult(context, NCEmergencyCareOutcome.PatientNotDown));
        }

        if (GetZoneTrauma(patient, profile.CprAssessmentZone) > profile.CprMaximumChestTrauma)
        {
            return CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr,
                NewResult(context, NCEmergencyCareOutcome.ChestTraumaContraindicated));
        }

        if (circulation.Perfusion > profile.CprMaximumNaturalPerfusion &&
            circulation.HeartRhythm is not NCHeartRhythm.ShockableArrest and
            not NCHeartRhythm.NonShockableArrest)
        {
            return CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr,
                NewResult(context, NCEmergencyCareOutcome.NotIndicated));
        }

        var previous = (float) state.CprRemaining.TotalSeconds;
        state.CprRemaining = Max(state.CprRemaining, profile.CprDuration);
        var result = new NCEmergencyCareResult(
            context.ResolutionId,
            NCEmergencyCareOutcome.Applied,
            previous,
            (float) state.CprRemaining.TotalSeconds,
            0);
        CompleteAction(patient, state, profile, context, NCEmergencyCareKind.Cpr, result);
        MarkPatientChanged(patient, context.ResolutionId, NCEmergencyCareKind.Cpr, result.Outcome);
        return result;
    }

    public NCEmergencyCareResult ApplyOxygen(
        EntityUid patient,
        EntityUid item,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return ApplyOxygen(patient, item, CreateActionContext(timestamp, actor, item));
    }

    public NCEmergencyCareResult ApplyOxygen(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context)
    {
        return ApplyTimedSupport(patient, item, context, NCEmergencyCareKind.Oxygen);
    }

    public NCEmergencyCareResult ApplyVentilation(
        EntityUid patient,
        EntityUid item,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return ApplyVentilation(patient, item, CreateActionContext(timestamp, actor, item));
    }

    public NCEmergencyCareResult ApplyVentilation(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context)
    {
        return ApplyTimedSupport(patient, item, context, NCEmergencyCareKind.Ventilation);
    }

    public NCEmergencyCareResult InfuseCrystalloid(
        EntityUid patient,
        EntityUid item,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return InfuseCrystalloid(patient, item, CreateActionContext(timestamp, actor, item));
    }

    public NCEmergencyCareResult InfuseCrystalloid(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context)
    {
        if (!TryPrepare(patient, context, out var state, out var profile, out var early))
            return early;
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        var enriched = EnrichContext(context, item);
        if (!TryGetCapability(item, NCEmergencyCareKind.Crystalloid, profile, out var itemState, out var capability))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Crystalloid,
                NewResult(enriched, NCEmergencyCareOutcome.UnsupportedItem));
        }

        enriched = enriched with { Capability = itemState.Capability };
        if (!HasCharges(itemState, capability))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Crystalloid,
                NewResult(enriched, NCEmergencyCareOutcome.DeviceDepleted));
        }

        var previous = Math.Clamp(state.CrystalloidVolume, 0f, profile.MaximumCrystalloidVolume);
        var delivered = Math.Clamp(capability.InfusionVolume, 0f, profile.MaximumCrystalloidVolume - previous);
        if (delivered <= 0f)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Crystalloid,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.VolumeAtCapacity,
                    previous, previous, 0));
        }

        var consumed = ConsumeCharges(itemState, capability);
        state.CrystalloidVolume = previous + delivered;
        var result = new NCEmergencyCareResult(
            enriched.ResolutionId,
            NCEmergencyCareOutcome.Applied,
            previous,
            state.CrystalloidVolume,
            consumed);
        CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Crystalloid, result);
        MarkPatientChanged(patient, enriched.ResolutionId, NCEmergencyCareKind.Crystalloid, result.Outcome);
        return result;
    }

    public NCEmergencyCareResult TransfuseBlood(
        EntityUid patient,
        EntityUid item,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return TransfuseBlood(patient, item, CreateActionContext(timestamp, actor, item));
    }

    public NCEmergencyCareResult TransfuseBlood(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context)
    {
        if (!TryPrepare(patient, context, out var state, out var profile, out var early))
            return early;
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        var enriched = EnrichContext(context, item);
        if (!TryGetCapability(item, NCEmergencyCareKind.BloodTransfusion, profile,
                out var itemState, out var capability) ||
            !TryComp<NCCirculationComponent>(patient, out var circulation))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion,
                NewResult(enriched, NCEmergencyCareOutcome.UnsupportedItem));
        }

        enriched = enriched with { Capability = itemState.Capability };
        if (!HasCharges(itemState, capability))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion,
                NewResult(enriched, NCEmergencyCareOutcome.DeviceDepleted));
        }

        var transfusion = _blood.TryTransfuse(
            patient,
            capability.DonorCompatibility,
            capability.InfusionVolume,
            profile.MaximumBloodVolumeMultiplier,
            profile.TransfusionCompatibility,
            markDirty: false);
        if (transfusion.Outcome == NCBloodTransfusionOutcome.Incompatible)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion,
                NewResult(enriched, NCEmergencyCareOutcome.IncompatibleBlood));
        }

        if (transfusion.Outcome == NCBloodTransfusionOutcome.AtCapacity)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.VolumeAtCapacity,
                    transfusion.PreviousVolume, transfusion.BloodVolume, 0));
        }

        if (transfusion.Outcome != NCBloodTransfusionOutcome.Applied)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion,
                NewResult(enriched, NCEmergencyCareOutcome.InvalidProfile));
        }

        var consumed = ConsumeCharges(itemState, capability);
        var result = new NCEmergencyCareResult(
            enriched.ResolutionId,
            NCEmergencyCareOutcome.Applied,
            transfusion.PreviousVolume,
            transfusion.BloodVolume,
            consumed);
        CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.BloodTransfusion, result);
        MarkPatientChanged(patient, enriched.ResolutionId, NCEmergencyCareKind.BloodTransfusion, result.Outcome);
        return result;
    }

    public NCEmergencyCareResult Defibrillate(
        EntityUid patient,
        EntityUid item,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        return Defibrillate(patient, item, CreateActionContext(timestamp, actor, item));
    }

    public NCEmergencyCareResult Defibrillate(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context)
    {
        if (!TryPrepare(patient, context, out var state, out var profile, out var early))
            return early;
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        var enriched = EnrichContext(context, item);
        if (!TryGetCapability(item, NCEmergencyCareKind.Defibrillation, profile,
                out var itemState, out var capability) ||
            !TryComp<NCCirculationComponent>(patient, out var circulation) ||
            !TryComp<NCPhysiologyComponent>(patient, out var physiology))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation,
                NewResult(enriched, NCEmergencyCareOutcome.UnsupportedItem));
        }

        enriched = enriched with { Capability = itemState.Capability };
        if (!HasCharges(itemState, capability))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation,
                NewResult(enriched, NCEmergencyCareOutcome.DeviceDepleted));
        }

        // A physical discharge consumes a charge even when the measured rhythm is not shockable.
        var previousRhythm = (float) circulation.HeartRhythm;
        var consumed = ConsumeCharges(itemState, capability);
        if (physiology.Irreversible)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.IrreversiblePatient,
                    previousRhythm, previousRhythm, consumed));
        }

        if (!HasViableVitalOrgan(patient, NCOrganFunctionType.Circulation) ||
            !HasViableVitalOrgan(patient, NCOrganFunctionType.Brain))
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.VitalOrganDestroyed,
                    previousRhythm, previousRhythm, consumed));
        }

        if (circulation.HeartRhythm != NCHeartRhythm.ShockableArrest)
        {
            return CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.WrongRhythm,
                    previousRhythm, previousRhythm, consumed));
        }

        // Rhythm conversion only removes the shockable rhythm. Blood volume, heart capacity and brain state
        // are deliberately untouched, so a destroyed or exsanguinated body cannot be revived by this action.
        _systemic.ResolveDefibrillation(patient);
        circulation.HeartRhythm = NCHeartRhythm.Normal;
        var result = new NCEmergencyCareResult(
            enriched.ResolutionId,
            NCEmergencyCareOutcome.Applied,
            previousRhythm,
            (float) circulation.HeartRhythm,
            consumed);
        CompleteAction(patient, state, profile, enriched, NCEmergencyCareKind.Defibrillation, result);
        MarkPatientChanged(patient, enriched.ResolutionId, NCEmergencyCareKind.Defibrillation, result.Outcome);
        TryApplyDefibrillationBurn(patient, enriched, profile);
        return result;
    }

    /// <summary>
    /// Returns the intervention snapshot consumed by a single physiology evaluation. Invalid or absent
    /// emergency state degrades to no support without weakening medical authority.
    /// </summary>
    public NCEmergencyPhysiologySupport GetPhysiologySupport(EntityUid patient, TimeSpan elapsed)
    {
        if (!TryComp<NCEmergencyCareStateComponent>(patient, out var state) ||
            !TryGetProfile(state, out var profile))
        {
            return new NCEmergencyPhysiologySupport(false, 0f, 1f, 0f, 0f);
        }

        var cprFraction = ActiveFraction(state.CprRemaining, elapsed);
        var oxygenFraction = ActiveFraction(state.OxygenRemaining, elapsed);
        var ventilationFraction = ActiveFraction(state.VentilationRemaining, elapsed);
        var cpr = cprFraction > 0f;
        var respiratorySupport = 0f;
        if (oxygenFraction > 0f)
            respiratorySupport = MathF.Max(
                respiratorySupport,
                profile.OxygenRespiratorySupport * oxygenFraction);
        if (ventilationFraction > 0f)
            respiratorySupport = MathF.Max(
                respiratorySupport,
                profile.VentilationRespiratorySupport * ventilationFraction);

        return new NCEmergencyPhysiologySupport(
            cpr,
            cpr ? profile.CprAssistedPerfusion * cprFraction : 0f,
            cpr
                ? profile.CprBrainInjuryRateMultiplier * cprFraction + 1f - cprFraction
                : 1f,
            Math.Clamp(respiratorySupport, 0f, 1f),
            Math.Clamp(state.CrystalloidVolume, 0f, profile.MaximumCrystalloidVolume));
    }

    /// <summary>
    /// Advances only one already-selected body. Returns true when support expired at this scheduler boundary.
    /// </summary>
    public bool AdvanceInterventions(EntityUid patient, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || !TryComp<NCEmergencyCareStateComponent>(patient, out var state))
            return false;

        var expired = false;
        expired |= AdvanceTimer(ref state.CprRemaining, elapsed);
        expired |= AdvanceTimer(ref state.OxygenRemaining, elapsed);
        expired |= AdvanceTimer(ref state.VentilationRemaining, elapsed);
        if (expired)
        {
            _medicalBodies.TryMarkDirty(
                patient,
                NCMedicalDirtyDomains.Vascular |
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence,
                false);
        }

        return expired;
    }

    public TimeSpan? GetNextInterventionDelay(EntityUid patient)
    {
        if (!TryComp<NCEmergencyCareStateComponent>(patient, out var state))
            return null;

        TimeSpan? result = null;
        IncludePositiveMinimum(state.CprRemaining, ref result);
        IncludePositiveMinimum(state.OxygenRemaining, ref result);
        IncludePositiveMinimum(state.VentilationRemaining, ref result);
        return result;
    }

    private NCEmergencyCareResult ApplyTimedSupport(
        EntityUid patient,
        EntityUid item,
        in NCEmergencyCareActionContext context,
        NCEmergencyCareKind kind)
    {
        if (!TryPrepare(patient, context, out var state, out var profile, out var early))
            return early;
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        var enriched = EnrichContext(context, item);
        if (!TryGetCapability(item, kind, profile, out var itemState, out var capability))
        {
            return CompleteAction(patient, state, profile, enriched, kind,
                NewResult(enriched, NCEmergencyCareOutcome.UnsupportedItem));
        }

        enriched = enriched with { Capability = itemState.Capability };
        if (!HasCharges(itemState, capability))
        {
            return CompleteAction(patient, state, profile, enriched, kind,
                NewResult(enriched, NCEmergencyCareOutcome.DeviceDepleted));
        }

        ref var remaining = ref kind == NCEmergencyCareKind.Oxygen
            ? ref state.OxygenRemaining
            : ref state.VentilationRemaining;
        var previous = (float) remaining.TotalSeconds;
        var next = Max(remaining, capability.SupportDuration);
        if (next <= remaining)
        {
            return CompleteAction(patient, state, profile, enriched, kind,
                new NCEmergencyCareResult(enriched.ResolutionId, NCEmergencyCareOutcome.NotIndicated,
                    previous, previous, 0));
        }

        var consumed = ConsumeCharges(itemState, capability);
        remaining = next;
        var result = new NCEmergencyCareResult(
            enriched.ResolutionId,
            NCEmergencyCareOutcome.Applied,
            previous,
            (float) remaining.TotalSeconds,
            consumed);
        CompleteAction(patient, state, profile, enriched, kind, result);
        MarkPatientChanged(patient, enriched.ResolutionId, kind, result.Outcome);
        return result;
    }

    private bool TryPrepare(
        EntityUid patient,
        in NCEmergencyCareActionContext context,
        out NCEmergencyCareStateComponent state,
        out NCEmergencyCareProfilePrototype profile,
        out NCEmergencyCareResult early)
    {
        state = default!;
        profile = default!;
        early = NewResult(context, NCEmergencyCareOutcome.PatientNotAuthoritative);
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return false;

        if (!context.ResolutionId.IsValid)
        {
            early = NewResult(context, NCEmergencyCareOutcome.InvalidAction);
            return false;
        }

        state = EnsureComp<NCEmergencyCareStateComponent>(patient);
        if (string.IsNullOrWhiteSpace(state.Profile.Id))
            state.Profile = GetDefaultProfile().ID;
        if (!TryGetProfile(state, out profile))
        {
            early = NewResult(context, NCEmergencyCareOutcome.InvalidProfile);
            return false;
        }

        return true;
    }

    private NCEmergencyCareProfilePrototype GetDefaultProfile()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCEmergencyCareProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();
        if (profiles.Count != 1)
            throw new InvalidOperationException("Exactly one default human ncEmergencyCareProfile must be defined.");
        return profiles[0];
    }

    private bool TryGetProfile(
        NCEmergencyCareStateComponent state,
        out NCEmergencyCareProfilePrototype profile)
    {
        if (string.IsNullOrWhiteSpace(state.Profile.Id) ||
            !_prototypes.TryIndex(state.Profile, out profile!))
        {
            profile = default!;
            return false;
        }

        return ValidateProfile(profile);
    }

    private bool ValidateProfile(NCEmergencyCareProfilePrototype profile)
    {
        return profile.CprDuration > TimeSpan.Zero &&
               profile.CprApplicationDuration > TimeSpan.Zero &&
               profile.CprInteractionRange > 0f &&
               IsUnit(profile.CprAssistedPerfusion) &&
               profile.CprAssistedPerfusion > 0f &&
               IsUnit(profile.CprBrainInjuryRateMultiplier) &&
               profile.CprBrainInjuryRateMultiplier > 0f &&
               IsUnit(profile.CprMaximumNaturalPerfusion) &&
               float.IsFinite(profile.CprMaximumChestTrauma) &&
               profile.CprMaximumChestTrauma >= 0f &&
               IsUnit(profile.OxygenRespiratorySupport) &&
               IsUnit(profile.VentilationRespiratorySupport) &&
               float.IsFinite(profile.MaximumCrystalloidVolume) &&
               profile.MaximumCrystalloidVolume >= 0f &&
               float.IsFinite(profile.MaximumBloodVolumeMultiplier) &&
               profile.MaximumBloodVolumeMultiplier > 0f &&
               profile.MaximumBloodVolumeMultiplier <= 1f &&
               profile.DefibrillationBurnDamage >= 0f &&
               float.IsFinite(profile.DefibrillationBurnDamage) &&
               profile.ProcessedActionCapacity > 0 &&
               profile.AuditHistoryCapacity > 0 &&
               IsCompatibilityMapValid(profile.TransfusionCompatibility) &&
               _prototypes.TryIndex<NCTargetZonePrototype>(profile.CprAssessmentZone, out _) &&
               _prototypes.TryIndex<NCTargetZonePrototype>(profile.DefibrillationBurnZone, out _) &&
               _prototypes.TryIndex<NCCombatDamageProfilePrototype>(profile.DefibrillationBurnProfile, out _) &&
               _prototypes.TryIndex(profile.DefibrillationBurnDamageType, out _);
    }

    private bool TryGetCapability(
        EntityUid item,
        NCEmergencyCareKind expectedKind,
        NCEmergencyCareProfilePrototype profile,
        out NCEmergencyCareItemComponent itemState,
        out NCEmergencyCareCapabilityPrototype capability)
    {
        itemState = default!;
        capability = default!;
        if (!TryComp<NCEmergencyCareItemComponent>(item, out var resolvedItem) ||
            !_prototypes.TryIndex(resolvedItem.Capability, out capability!) ||
            capability.Kind != expectedKind ||
            capability.Profile.Id != profile.ID ||
            capability.ChargeCost <= 0 ||
            capability.ApplicationDuration <= TimeSpan.Zero ||
            capability.InteractionRange <= 0f ||
            (expectedKind is NCEmergencyCareKind.Oxygen or NCEmergencyCareKind.Ventilation &&
             capability.SupportDuration <= TimeSpan.Zero) ||
            (expectedKind is NCEmergencyCareKind.Crystalloid or NCEmergencyCareKind.BloodTransfusion &&
             (!float.IsFinite(capability.InfusionVolume) || capability.InfusionVolume <= 0f)))
        {
            return false;
        }

        itemState = resolvedItem;
        return expectedKind != NCEmergencyCareKind.BloodTransfusion ||
               !string.IsNullOrWhiteSpace(capability.DonorCompatibility);
    }

    private static bool HasCharges(
        NCEmergencyCareItemComponent item,
        NCEmergencyCareCapabilityPrototype capability)
    {
        return item.ChargesRemaining >= capability.ChargeCost;
    }

    private static int ConsumeCharges(
        NCEmergencyCareItemComponent itemState,
        NCEmergencyCareCapabilityPrototype capability)
    {
        itemState.ChargesRemaining -= capability.ChargeCost;
        // Exact device charges are server-owned. This component is deliberately not networked,
        // so there is no component state to dirty after the authoritative mutation.
        return capability.ChargeCost;
    }

    private bool HasViableVitalOrgan(EntityUid patient, NCOrganFunctionType functionType)
    {
        if (!TryComp<BodyComponent>(patient, out var body) || body.Organs == null)
            return false;

        foreach (var organ in body.Organs.ContainedEntities)
        {
            if (!TryComp<NCOrganFunctionComponent>(organ, out var function) ||
                function.FunctionType != functionType)
            {
                continue;
            }

            // Recoverable describes whether lost capacity can heal, not whether the organ is presently alive.
            // A healthy biological brain is deliberately non-recoverable but remains viable above threshold.
            if (function.CurrentCapacity > function.NonFunctionalThreshold)
                return true;
        }

        return false;
    }

    private float GetZoneTrauma(EntityUid patient, ProtoId<NCTargetZonePrototype> zone)
    {
        if (!TryComp<NCWoundContainerComponent>(patient, out var wounds))
            return 0f;

        var total = wounds.OverflowTraumaByZone.TryGetValue(zone, out var overflow)
            ? MathF.Max(0f, overflow)
            : 0f;
        foreach (var wound in wounds.Wounds.Values)
        {
            if (wound.TargetZone == zone)
                total += MathF.Max(0f, wound.IntegrityDamage.Float());
        }

        return total;
    }

    private void TryApplyDefibrillationBurn(
        EntityUid patient,
        in NCEmergencyCareActionContext context,
        NCEmergencyCareProfilePrototype profile)
    {
        if (profile.DefibrillationBurnDamage <= 0f ||
            !_prototypes.TryIndex(profile.DefibrillationBurnDamageType, out var damageType))
        {
            return;
        }

        var damage = new DamageSpecifier(damageType, profile.DefibrillationBurnDamage);
        var impact = new NCResolvedCombatImpact(
            context.ResolutionId,
            0,
            1,
            context.ResolutionId.DeriveSeed(1),
            patient,
            null,
            null,
            null,
            patient,
            NCTargetingMode.Automatic,
            null,
            profile.DefibrillationBurnProfile,
            damage,
            Vector2.Zero,
            Vector2.UnitY,
            0f,
            context.Timestamp)
        {
            ForcedZone = profile.DefibrillationBurnZone,
        };
        _trauma.TryDeliver(impact, out _);
    }

    private NCEmergencyCareActionContext CreateActionContext(
        TimeSpan? timestamp,
        EntityUid? actor = null,
        EntityUid? item = null)
    {
        return new NCEmergencyCareActionContext(
            NCActionResolutionId.New(),
            timestamp ?? _timing.CurTime,
            GetPrototypeId(actor),
            GetPrototypeId(item));
    }

    private NCEmergencyCareActionContext EnrichContext(
        in NCEmergencyCareActionContext context,
        EntityUid item)
    {
        return context with { ItemPrototype = GetPrototypeId(item) ?? context.ItemPrototype };
    }

    private EntProtoId? GetPrototypeId(EntityUid? entity)
    {
        if (entity is not { } uid ||
            !TryComp(uid, out MetaDataComponent? metadata) ||
            metadata.EntityPrototype?.ID is not { } id)
        {
            return null;
        }

        return id;
    }

    private static bool TryGetDuplicate(
        NCEmergencyCareStateComponent state,
        in NCEmergencyCareActionContext context,
        out NCEmergencyCareResult result)
    {
        result = default;
        if (!state.ProcessedActions.Contains(context.ResolutionId))
            return false;

        var resolutionId = context.ResolutionId;
        var audit = state.AuditHistory.LastOrDefault(entry => entry.ResolutionId == resolutionId);
        result = audit == null
            ? NewResult(context, NCEmergencyCareOutcome.DuplicateAction)
            : new NCEmergencyCareResult(
                context.ResolutionId,
                NCEmergencyCareOutcome.DuplicateAction,
                audit.PreviousValue,
                audit.CurrentValue,
                0);
        return true;
    }

    private NCEmergencyCareResult CompleteAction(
        EntityUid patient,
        NCEmergencyCareStateComponent state,
        NCEmergencyCareProfilePrototype profile,
        in NCEmergencyCareActionContext context,
        NCEmergencyCareKind kind,
        in NCEmergencyCareResult result)
    {
        state.ProcessedActions.Add(context.ResolutionId);
        while (state.ProcessedActions.Count > profile.ProcessedActionCapacity)
            state.ProcessedActions.RemoveAt(0);

        state.AuditHistory.Add(new NCEmergencyCareAuditRecord
        {
            ResolutionId = context.ResolutionId,
            Kind = kind,
            Outcome = result.Outcome,
            PreviousValue = result.PreviousValue,
            CurrentValue = result.CurrentValue,
            ChargesConsumed = result.ChargesConsumed,
            ActorPrototype = context.ActorPrototype,
            ItemPrototype = context.ItemPrototype,
            Capability = context.Capability,
            Timestamp = context.Timestamp,
        });
        while (state.AuditHistory.Count > profile.AuditHistoryCapacity)
            state.AuditHistory.RemoveAt(0);

        _medicalBodies.TryMarkDirty(
            patient,
            NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence,
            false);
        return result;
    }

    private void MarkPatientChanged(
        EntityUid patient,
        NCActionResolutionId resolutionId,
        NCEmergencyCareKind kind,
        NCEmergencyCareOutcome outcome)
    {
        _medicalBodies.TryMarkDirty(
            patient,
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
        var changed = new NCEmergencyCareChangedEvent(resolutionId, kind, outcome);
        RaiseLocalEvent(patient, ref changed);
    }

    private static NCEmergencyCareResult NewResult(
        in NCEmergencyCareActionContext context,
        NCEmergencyCareOutcome outcome)
    {
        return new NCEmergencyCareResult(context.ResolutionId, outcome, 0f, 0f, 0);
    }

    private static bool AdvanceTimer(ref TimeSpan remaining, TimeSpan elapsed)
    {
        if (remaining <= TimeSpan.Zero)
            return false;

        remaining = remaining > elapsed ? remaining - elapsed : TimeSpan.Zero;
        return remaining == TimeSpan.Zero;
    }

    private static void IncludePositiveMinimum(TimeSpan value, ref TimeSpan? current)
    {
        if (value <= TimeSpan.Zero)
            return;
        if (current == null || value < current)
            current = value;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }

    private static bool IsUnit(float value)
    {
        return float.IsFinite(value) && value >= 0f && value <= 1f;
    }

    private static bool IsCompatibilityMapValid(IReadOnlyDictionary<string, List<string>> compatibility)
    {
        if (compatibility.Count == 0)
            return false;

        foreach (var (donor, recipients) in compatibility)
        {
            if (string.IsNullOrWhiteSpace(donor) ||
                recipients.Count == 0 ||
                recipients.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }
        }

        return true;
    }

    private static float ActiveFraction(TimeSpan remaining, TimeSpan elapsed)
    {
        if (remaining <= TimeSpan.Zero)
            return 0f;
        if (elapsed <= TimeSpan.Zero || remaining >= elapsed)
            return 1f;
        return Math.Clamp((float) (remaining.TotalSeconds / elapsed.TotalSeconds), 0f, 1f);
    }
}
