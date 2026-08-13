// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Physiology;

/// <summary>
/// Deterministically derives perfusion, oxygenation, pain, shock and consciousness from authoritative
/// medical state. Damageable is deliberately not read or written here.
/// </summary>
public sealed partial class NCPhysiologySystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCEmergencyCareSystem _emergencyCare = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, MapInitEvent>(OnBodyMapInit);
        // NC medicine must win any upstream threshold proposal for authoritative human bodies.
        SubscribeLocalEvent<NCMedicalBodyComponent, UpdateMobStateEvent>(
            OnUpdateMobState,
            after: [typeof(MobThresholdSystem)]);
    }

    private void OnBodyMapInit(Entity<NCMedicalBodyComponent> entity, ref MapInitEvent args)
    {
        // NCMedicalBody has one MapInit owner. Initialize systemic state here as well so Robust's
        // component-event bus never receives a duplicate subscription for the same component/event pair.
        _systemic.EnsureInitialized(entity.Owner);
        EnsureInitialized(entity.Owner);
    }

    private void OnUpdateMobState(Entity<NCMedicalBodyComponent> entity, ref UpdateMobStateEvent args)
    {
        args.State = TryComp<NCPhysiologyComponent>(entity.Owner, out var physiology)
            ? ToMobState(physiology)
            : MobState.Alive;
    }

    public (NCCirculationComponent Circulation, NCPhysiologyComponent Physiology) EnsureInitialized(EntityUid body)
    {
        var circulation = EnsureComp<NCCirculationComponent>(body);
        var physiology = EnsureComp<NCPhysiologyComponent>(body);
        if (circulation.Initialized && !string.IsNullOrWhiteSpace(circulation.Profile.Id))
        {
            // Older authoritative snapshots predate the compatibility token. Migrate the empty value from
            // the same physiology profile instead of inventing a code-side blood type.
            if (string.IsNullOrWhiteSpace(circulation.TransfusionCompatibility) &&
                _prototypes.TryIndex(circulation.Profile, out NCPhysiologyProfilePrototype? existingProfile))
            {
                circulation.TransfusionCompatibility = existingProfile.DefaultTransfusionCompatibility;
            }
            return (circulation, physiology);
        }

        var profile = GetDefaultProfile();
        ValidateProfile(profile);
        circulation.Profile = profile.ID;
        circulation.Initialized = true;
        circulation.BloodVolume = profile.NominalBloodVolume;
        circulation.EffectiveCirculatingVolume = profile.NominalBloodVolume;
        circulation.CardiacOutput = 1f;
        circulation.Perfusion = 1f;
        circulation.BloodPressureState = NCBloodPressureState.Normal;
        circulation.HeartRhythm = NCHeartRhythm.Normal;
        circulation.TransfusionCompatibility = profile.DefaultTransfusionCompatibility;
        circulation.LastEvaluation = _timing.CurTime;
        return (circulation, physiology);
    }

    public NCPhysiologyProfilePrototype GetProfile(EntityUid body)
    {
        var (circulation, _) = EnsureInitialized(body);
        return _prototypes.Index(circulation.Profile);
    }

    public bool ResetBody(EntityUid body)
    {
        if (!HasComp<NCMedicalBodyComponent>(body))
            return false;

        var profile = GetDefaultProfile();
        ValidateProfile(profile);
        var circulation = EnsureComp<NCCirculationComponent>(body);
        circulation.Profile = profile.ID;
        circulation.Initialized = true;
        circulation.BloodVolume = profile.NominalBloodVolume;
        circulation.EffectiveCirculatingVolume = profile.NominalBloodVolume;
        circulation.CardiacOutput = 1f;
        circulation.Perfusion = 1f;
        circulation.BloodPressureState = NCBloodPressureState.Normal;
        circulation.HeartRhythm = NCHeartRhythm.Normal;
        circulation.TransfusionCompatibility = profile.DefaultTransfusionCompatibility;
        circulation.LastEvaluation = _timing.CurTime;

        var physiology = EnsureComp<NCPhysiologyComponent>(body);
        physiology.Oxygenation = 1f;
        physiology.TotalPain = 0f;
        physiology.PainLevel = NCPainLevel.None;
        physiology.Shock = 0f;
        physiology.ShockStage = NCShockStage.None;
        physiology.Consciousness = NCConsciousnessState.Alert;
        physiology.Condition = NCPhysiologicalCondition.Stable;
        physiology.BrainFunction = 1f;
        physiology.TimeBelowCriticalPerfusion = TimeSpan.Zero;
        physiology.Irreversible = false;
        return true;
    }

    /// <summary>
    /// Applies an explicit trusted irreversible-death boundary without routing through compatibility HP.
    /// This is reserved for administrative/gameplay actions such as an accepted suicide command; ordinary
    /// combat and medical failures must reach the same state through measured physiology.
    /// </summary>
    public bool ForceIrreversibleDeath(EntityUid body)
    {
        if (!TryComp<NCMedicalBodyComponent>(body, out var medical))
            return false;

        var (circulation, physiology) = EnsureInitialized(body);
        var previousCondition = physiology.Condition;
        var previousConsciousness = physiology.Consciousness;
        SetDead(circulation, physiology);
        circulation.LastEvaluation = _timing.CurTime;

        _medicalBodies.TryMarkDirty(
            (body, medical),
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence,
            false);

        if (previousCondition != physiology.Condition ||
            previousConsciousness != physiology.Consciousness)
        {
            var changed = new NCPhysiologyChangedEvent(
                previousCondition,
                physiology.Condition,
                previousConsciousness,
                physiology.Consciousness);
            RaiseLocalEvent(body, ref changed);
        }

        _mobState.UpdateMobState(body);
        var evaluated = new NCPhysiologyEvaluatedEvent();
        RaiseLocalEvent(body, ref evaluated);
        return true;
    }

    /// <summary>
    /// Re-evaluates one body after an event or scheduler interval. Elapsed time is explicit so tests and
    /// test and restore callers use the exact same deterministic transition API.
    /// </summary>
    public bool EvaluateBody(EntityUid body, TimeSpan elapsed)
    {
        if (!TryComp<NCMedicalBodyComponent>(body, out var medical))
            return false;

        var (circulation, physiology) = EnsureInitialized(body);
        var profile = _prototypes.Index(circulation.Profile);
        ValidateProfile(profile);

        var previousCondition = physiology.Condition;
        var previousConsciousness = physiology.Consciousness;

        circulation.BloodVolume = Math.Clamp(circulation.BloodVolume, 0f, profile.NominalBloodVolume);
        var emergencySupport = _emergencyCare.GetPhysiologySupport(body, elapsed);
        circulation.EffectiveCirculatingVolume = MathF.Max(
            0f,
            circulation.BloodVolume + emergencySupport.CrystalloidVolume);
        var effectiveVolumeFraction = Math.Clamp(
            circulation.EffectiveCirculatingVolume / profile.NominalBloodVolume,
            0f,
            1f);

        if (physiology.Irreversible)
        {
            SetDead(circulation, physiology);
        }
        else
        {
            var vitalFunctions = GetVitalFunctions(body);
            var systemic = _systemic.GetPhysiologyEffects(body);
            var pharmacology = _pharmacology.GetModifiers(body);
            var volumeCardiacOutput = effectiveVolumeFraction <= profile.ArrestPerfusionThreshold
                ? 0f
                : Math.Clamp(effectiveVolumeFraction / profile.CriticalPerfusionThreshold, 0f, 1f);
            circulation.CardiacOutput = volumeCardiacOutput *
                                         vitalFunctions.Circulation *
                                         systemic.CardiacOutputMultiplier *
                                         pharmacology.CardiacOutputMultiplier;
            if (systemic.ShockableArrest && vitalFunctions.Circulation > 0f)
                circulation.CardiacOutput = 0f;
            var naturalPerfusion = circulation.CardiacOutput <= 0f
                ? 0f
                : Math.Clamp(effectiveVolumeFraction * circulation.CardiacOutput, 0f, 1f);
            circulation.Perfusion = Math.Clamp(
                MathF.Max(naturalPerfusion, emergencySupport.AssistedPerfusion),
                0f,
                1f);

            // Oxygen/ventilation can improve impaired respiratory transfer, but cannot transport oxygen
            // without perfusion. A metabolized synthetic carrier contributes capacity without pretending
            // crystalloid itself contains red cells.
            var respiratoryTransfer = MathF.Max(
                Math.Clamp(
                    vitalFunctions.Respiration * pharmacology.RespiratoryTransferMultiplier,
                    0f,
                    1f),
                emergencySupport.RespiratorySupport);
            var oxygenCarrierFraction = circulation.EffectiveCirculatingVolume > 0f
                ? Math.Clamp(
                    circulation.BloodVolume / circulation.EffectiveCirculatingVolume +
                    pharmacology.OxygenCarrierContribution,
                    0f,
                    1f)
                : 0f;
            physiology.Oxygenation = circulation.Perfusion <= 0f
                ? 0f
                : Math.Clamp(
                    MathF.Min(circulation.Perfusion, respiratoryTransfer) *
                    oxygenCarrierFraction *
                    systemic.OxygenationMultiplier *
                    pharmacology.OxygenationMultiplier,
                    0f,
                    1f);
            UpdatePressureAndRhythm(
                circulation,
                profile,
                systemic,
                pharmacology.RhythmInstability,
                pharmacology.RhythmStability,
                vitalFunctions.Circulation > 0f);

            physiology.TotalPain = CalculateTotalPain(body) * (1f - pharmacology.Analgesia);
            physiology.PainLevel = ResolvePain(physiology.TotalPain, profile.PainThresholds);
            var painShock = profile.PainThresholds.Count > 0
                ? Math.Clamp(physiology.TotalPain / profile.PainThresholds[^1], 0f, 1f) *
                  profile.PainShockContribution
                : 0f;
            // Crystalloid can support vascular filling and therefore hypovolemic shock, but oxygen carrying
            // capacity above remains tied exclusively to real blood volume.
            physiology.Shock = Math.Clamp(
                1f - effectiveVolumeFraction + painShock + systemic.ShockContribution +
                pharmacology.ShockContribution,
                0f,
                1f);
            physiology.ShockStage = ResolveShock(physiology.Shock, profile.ShockThresholds);

            UpdateBrainState(
                physiology,
                MathF.Min(circulation.Perfusion, physiology.Oxygenation),
                MathF.Min(
                    vitalFunctions.Brain,
                    MathF.Min(systemic.BrainFunctionMultiplier, pharmacology.BrainFunctionMultiplier)),
                elapsed,
                emergencySupport.BrainInjuryRateMultiplier,
                profile);
            physiology.Irreversible |= vitalFunctions.BrainNonFunctional;
            if (physiology.Irreversible)
            {
                SetDead(circulation, physiology);
            }
            else
            {
                physiology.Condition = circulation.Perfusion <= profile.ArrestPerfusionThreshold
                    ? NCPhysiologicalCondition.ClinicalArrest
                    : circulation.Perfusion <= profile.CriticalPerfusionThreshold ||
                      physiology.Oxygenation <= profile.CriticalPerfusionThreshold ||
                      physiology.BrainFunction <= profile.CriticalPerfusionThreshold ||
                      physiology.ShockStage >= NCShockStage.Severe
                        ? NCPhysiologicalCondition.Critical
                        : physiology.ShockStage != NCShockStage.None
                            ? NCPhysiologicalCondition.Compensating
                            : NCPhysiologicalCondition.Stable;

                var consciousnessScore = Math.Clamp(
                    MathF.Min(
                        MathF.Min(circulation.Perfusion, physiology.Oxygenation),
                        physiology.BrainFunction) -
                    physiology.Shock * profile.ShockConsciousnessPenalty -
                    MathF.Max(
                        0f,
                        MathF.Max(pharmacology.Sedation, pharmacology.Anesthesia) -
                        pharmacology.Arousal),
                    0f,
                    1f);
                physiology.Consciousness = ResolveConsciousness(consciousnessScore, profile);
                if (physiology.Condition == NCPhysiologicalCondition.ClinicalArrest &&
                    physiology.Consciousness < NCConsciousnessState.Comatose)
                {
                    physiology.Consciousness = NCConsciousnessState.Comatose;
                }
            }
        }

        circulation.LastEvaluation += elapsed;
        _medicalBodies.TryClearDirty(
            (body, medical),
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness);

        if (previousCondition == physiology.Condition && previousConsciousness == physiology.Consciousness)
        {
            _mobState.UpdateMobState(body);
            var evaluated = new NCPhysiologyEvaluatedEvent();
            RaiseLocalEvent(body, ref evaluated);
            return true;
        }

        _medicalBodies.TryMarkDirty(
            (body, medical),
            NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence,
            false);

        var changed = new NCPhysiologyChangedEvent(
            previousCondition,
            physiology.Condition,
            previousConsciousness,
            physiology.Consciousness);
        RaiseLocalEvent(body, ref changed);
        _mobState.UpdateMobState(body);
        var evaluatedAfterChange = new NCPhysiologyEvaluatedEvent();
        RaiseLocalEvent(body, ref evaluatedAfterChange);
        return true;
    }

    private NCPhysiologyProfilePrototype GetDefaultProfile()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCPhysiologyProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();
        if (profiles.Count != 1)
            throw new InvalidOperationException("Exactly one default human ncPhysiologyProfile must be defined.");
        return profiles[0];
    }

    private float CalculateTotalPain(EntityUid body)
    {
        if (!TryComp<NCWoundContainerComponent>(body, out var wounds))
            return 0f;

        var total = MathF.Max(0f, wounds.OverflowPain);
        foreach (var wound in wounds.Wounds.Values)
            total += MathF.Max(0f, wound.Pain);
        return total;
    }

    private NCVitalFunctions GetVitalFunctions(EntityUid body)
    {
        var circulation = 1f;
        var respiration = 1f;
        var brain = 1f;
        var brainNonFunctional = false;
        var hasCirculation = false;
        var hasRespiration = false;
        var hasBrain = false;
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return new NCVitalFunctions(0f, 0f, 0f, true);

        // One body's organ container is small and bounded; this runs only when the body scheduler evaluates
        // medical dirty state, never as an independent per-organ Update.
        foreach (var organ in bodyComponent.Organs.ContainedEntities)
        {
            if (!TryComp<NCOrganFunctionComponent>(organ, out var function))
                continue;

            var capacity = function.BaseCapacity > 0f
                ? Math.Clamp(function.CurrentCapacity / function.BaseCapacity, 0f, 1f)
                : 0f;
            switch (function.FunctionType)
            {
                case NCOrganFunctionType.Circulation:
                    hasCirculation = true;
                    circulation = MathF.Min(circulation, capacity);
                    break;
                case NCOrganFunctionType.Respiration:
                    hasRespiration = true;
                    respiration = MathF.Min(respiration, capacity);
                    break;
                case NCOrganFunctionType.Brain:
                    hasBrain = true;
                    brain = MathF.Min(brain, capacity);
                    brainNonFunctional |= !function.Recoverable &&
                                          capacity <= function.NonFunctionalThreshold;
                    break;
            }
        }

        // Missing vital organs are physiological absence, not a healthy default. Detachment changes the
        // live body container first, so this bounded evaluation also covers surgery and limb/organ loss.
        return new NCVitalFunctions(
            hasCirculation ? circulation : 0f,
            hasRespiration ? respiration : 0f,
            hasBrain ? brain : 0f,
            brainNonFunctional || !hasBrain);
    }

    private static void UpdatePressureAndRhythm(
        NCCirculationComponent circulation,
        NCPhysiologyProfilePrototype profile,
        NCSystemicPhysiologyEffects systemic,
        float pharmacologicalInstability,
        float rhythmStability,
        bool viableHeart)
    {
        var perfusion = circulation.Perfusion;
        if (systemic.ShockableArrest && viableHeart)
        {
            circulation.BloodPressureState = NCBloodPressureState.Absent;
            circulation.HeartRhythm = NCHeartRhythm.ShockableArrest;
            return;
        }

        if (perfusion <= profile.ArrestPerfusionThreshold)
        {
            circulation.BloodPressureState = NCBloodPressureState.Absent;
            circulation.HeartRhythm = NCHeartRhythm.NonShockableArrest;
        }
        else if (perfusion <= profile.CriticalPerfusionThreshold)
        {
            circulation.BloodPressureState = NCBloodPressureState.Critical;
            circulation.HeartRhythm = NCHeartRhythm.Unstable;
        }
        else if (MathF.Max(
                     systemic.UnstableRhythm ? systemic.RhythmInstability : 0f,
                     pharmacologicalInstability) > rhythmStability)
        {
            circulation.BloodPressureState = NCBloodPressureState.Critical;
            circulation.HeartRhythm = NCHeartRhythm.Unstable;
        }
        else if (perfusion <= profile.ConfusedThreshold)
        {
            circulation.BloodPressureState = NCBloodPressureState.Hypotensive;
            circulation.HeartRhythm = NCHeartRhythm.Tachycardia;
        }
        else if (perfusion <= profile.DazedThreshold)
        {
            circulation.BloodPressureState = NCBloodPressureState.CompensatedLow;
            circulation.HeartRhythm = NCHeartRhythm.Tachycardia;
        }
        else
        {
            circulation.BloodPressureState = NCBloodPressureState.Normal;
            circulation.HeartRhythm = NCHeartRhythm.Normal;
        }
    }

    private static void UpdateBrainState(
        NCPhysiologyComponent physiology,
        float perfusion,
        float directBrainFunction,
        TimeSpan elapsed,
        float brainInjuryRateMultiplier,
        NCPhysiologyProfilePrototype profile)
    {
        if (perfusion <= profile.BrainInjuryPerfusionThreshold)
        {
            var rate = Math.Clamp(brainInjuryRateMultiplier, 0f, 1f);
            physiology.TimeBelowCriticalPerfusion += TimeSpan.FromSeconds(
                Math.Max(0d, elapsed.TotalSeconds * rate));
        }
        else
        {
            physiology.TimeBelowCriticalPerfusion = TimeSpan.FromSeconds(Math.Max(
                0d,
                physiology.TimeBelowCriticalPerfusion.TotalSeconds -
                elapsed.TotalSeconds * profile.BrainRecoveryRate));
        }

        var deathSeconds = Math.Max(0.001, profile.BrainDeathDelay.TotalSeconds);
        physiology.BrainFunction = MathF.Min(
            Math.Clamp(
                1f - (float) (physiology.TimeBelowCriticalPerfusion.TotalSeconds / deathSeconds),
                0f,
                1f),
            directBrainFunction);
        if (physiology.TimeBelowCriticalPerfusion >= profile.BrainDeathDelay)
            physiology.Irreversible = true;
    }

    private static void SetDead(NCCirculationComponent circulation, NCPhysiologyComponent physiology)
    {
        physiology.Irreversible = true;
        physiology.BrainFunction = 0f;
        physiology.Condition = NCPhysiologicalCondition.Dead;
        physiology.Consciousness = NCConsciousnessState.BrainDead;
        circulation.CardiacOutput = 0f;
        circulation.Perfusion = 0f;
        circulation.BloodPressureState = NCBloodPressureState.Absent;
        circulation.HeartRhythm = NCHeartRhythm.NonShockableArrest;
    }

    private static MobState ToMobState(NCPhysiologyComponent physiology)
    {
        if (physiology.Condition == NCPhysiologicalCondition.Dead ||
            physiology.Consciousness == NCConsciousnessState.BrainDead)
        {
            return MobState.Dead;
        }

        if (physiology.Condition is NCPhysiologicalCondition.Critical or
            NCPhysiologicalCondition.ClinicalArrest ||
            physiology.Consciousness >= NCConsciousnessState.Unconscious)
        {
            return MobState.Critical;
        }

        return MobState.Alive;
    }

    private static NCPainLevel ResolvePain(float value, IReadOnlyList<float> thresholds)
    {
        if (thresholds.Count != 5)
            throw new InvalidOperationException("NC pain thresholds must contain exactly five values.");

        var level = NCPainLevel.None;
        for (var i = 0; i < thresholds.Count; i++)
        {
            if (value < thresholds[i])
                break;
            level = (NCPainLevel) (i + 1);
        }
        return level;
    }

    private static NCShockStage ResolveShock(float value, IReadOnlyList<float> thresholds)
    {
        if (thresholds.Count != 4)
            throw new InvalidOperationException("NC shock thresholds must contain exactly four values.");

        var stage = NCShockStage.None;
        for (var i = 0; i < thresholds.Count; i++)
        {
            if (value < thresholds[i])
                break;
            stage = (NCShockStage) (i + 1);
        }
        return stage;
    }

    private static NCConsciousnessState ResolveConsciousness(
        float score,
        NCPhysiologyProfilePrototype profile)
    {
        if (score <= profile.ComatoseThreshold)
            return NCConsciousnessState.Comatose;
        if (score <= profile.UnconsciousThreshold)
            return NCConsciousnessState.Unconscious;
        if (score <= profile.StuporousThreshold)
            return NCConsciousnessState.Stuporous;
        if (score <= profile.ConfusedThreshold)
            return NCConsciousnessState.Confused;
        if (score <= profile.DazedThreshold)
            return NCConsciousnessState.Dazed;
        return NCConsciousnessState.Alert;
    }

    private static void ValidateProfile(NCPhysiologyProfilePrototype profile)
    {
        if (profile.NominalBloodVolume <= 0f ||
            string.IsNullOrWhiteSpace(profile.DefaultTransfusionCompatibility) ||
            profile.PainThresholds.Count != 5 ||
            profile.ShockThresholds.Count != 4 ||
            profile.BrainDeathDelay <= TimeSpan.Zero ||
            !IsStrictlyAscending(profile.PainThresholds) ||
            !IsStrictlyAscending(profile.ShockThresholds) ||
            profile.ArrestPerfusionThreshold < 0f ||
            profile.ArrestPerfusionThreshold >= profile.CriticalPerfusionThreshold ||
            profile.CriticalPerfusionThreshold > 1f ||
            profile.ComatoseThreshold < 0f ||
            profile.ComatoseThreshold >= profile.UnconsciousThreshold ||
            profile.UnconsciousThreshold >= profile.StuporousThreshold ||
            profile.StuporousThreshold >= profile.ConfusedThreshold ||
            profile.ConfusedThreshold >= profile.DazedThreshold ||
            profile.DazedThreshold > 1f ||
            profile.CriticalUpdateInterval <= TimeSpan.Zero ||
            profile.ActiveBleedingUpdateInterval <= TimeSpan.Zero ||
            profile.StableInjuredUpdateInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"NC physiology profile {profile.ID} has invalid thresholds.");
        }
    }

    private static bool IsStrictlyAscending(IReadOnlyList<float> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (!float.IsFinite(values[i - 1]) || values[i] <= values[i - 1])
                return false;
        }
        return values.Count == 0 || float.IsFinite(values[^1]);
    }

    private readonly record struct NCVitalFunctions(
        float Circulation,
        float Respiration,
        float Brain,
        bool BrainNonFunctional);
}
