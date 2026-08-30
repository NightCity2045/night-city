// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Domains;

/// <summary>
/// Converts the authoritative materialized medical graph into a bounded A-Life projection.
/// It never reconstructs health from the strategic fields: restoration always uses the opaque medical payload.
/// </summary>
public sealed partial class NCCityActorHealthAdapterSystem : EntitySystem
{
    private static readonly ProtoId<NCCityActorSummaryPolicyPrototype> DefaultPolicy =
        new("NCDefaultCityActorSummaryPolicy");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalPersistenceSystem _medical = default!;

    public bool TryCapture(
        EntityUid body,
        NCActorId actorId,
        long sourceRevision,
        NCActiveWorldTime now,
        Guid eventId,
        out NCCityActorHealthSnapshot result,
        out string? error)
    {
        result = default!;
        error = null;
        if (!actorId.IsValid || sourceRevision < 0 || !now.IsValid || eventId == Guid.Empty ||
            !TryComp<NCBodyFunctionComponent>(body, out var bodyFunction))
        {
            error = "The actor health capture identity, time, event, or body-function projection is invalid.";
            return false;
        }

        if (!_medical.TryCapturePersistentActorState(
                body,
                actorId,
                sourceRevision,
                out var medical,
                out var payload))
        {
            error = "The authoritative NC medical graph could not be captured for this actor.";
            return false;
        }

        var policy = _prototypes.Index(DefaultPolicy);
        var summary = new NCCityActorHealthSummary(
            MapCondition(medical.Physiology.Condition),
            ClassifyBleeding(medical.Bleeding, policy.ActiveBleedingRateThreshold),
            ClassifyMobility(bodyFunction.Movement, policy),
            MapConsciousness(medical.Physiology.Consciousness),
            GetCriticalWounds(medical),
            GetActiveTreatment(medical),
            ClassifyCyberware(medical, policy),
            null);

        result = new NCCityActorHealthSnapshot(
            actorId,
            summary,
            medical.Version,
            payload,
            now,
            1,
            eventId);
        return true;
    }

    private static NCCityOverallCondition MapCondition(NCPhysiologicalCondition condition)
    {
        return condition switch
        {
            NCPhysiologicalCondition.Stable => NCCityOverallCondition.Stable,
            NCPhysiologicalCondition.Compensating => NCCityOverallCondition.Compensating,
            NCPhysiologicalCondition.Critical => NCCityOverallCondition.Critical,
            NCPhysiologicalCondition.ClinicalArrest => NCCityOverallCondition.ClinicalArrest,
            NCPhysiologicalCondition.Dead => NCCityOverallCondition.Dead,
            _ => NCCityOverallCondition.Critical,
        };
    }

    private static NCCityConsciousnessState MapConsciousness(NCConsciousnessState consciousness)
    {
        return consciousness switch
        {
            NCConsciousnessState.Alert => NCCityConsciousnessState.Alert,
            NCConsciousnessState.Dazed => NCCityConsciousnessState.Dazed,
            NCConsciousnessState.Confused => NCCityConsciousnessState.Confused,
            NCConsciousnessState.Stuporous => NCCityConsciousnessState.Stuporous,
            NCConsciousnessState.Unconscious => NCCityConsciousnessState.Unconscious,
            NCConsciousnessState.Comatose => NCCityConsciousnessState.Comatose,
            NCConsciousnessState.BrainDead => NCCityConsciousnessState.BrainDead,
            _ => NCCityConsciousnessState.Unconscious,
        };
    }

    private static NCCityMobilityState ClassifyMobility(
        float movement,
        NCCityActorSummaryPolicyPrototype policy)
    {
        if (!float.IsFinite(movement) || movement <= policy.ImmobileMovementThreshold)
            return NCCityMobilityState.Immobile;
        return movement < policy.ImpairedMovementThreshold
            ? NCCityMobilityState.Impaired
            : NCCityMobilityState.Mobile;
    }

    private static Guid[] GetCriticalWounds(NCMedicalStateSnapshot medical)
    {
        return medical.Wounds?.Records.Values
                   .Where(static wound => wound.Severity >= NCWoundSeverity.Critical)
                   .Select(static wound => wound.WoundId)
                   .Where(static id => id != Guid.Empty)
                   .Distinct()
                   .OrderBy(static id => id)
                   .Take(NCMedicalPersistenceSystem.MaxWoundRecords)
                   .ToArray()
               ?? [];
    }

    private static NCCityTreatmentState GetActiveTreatment(NCMedicalStateSnapshot medical)
    {
        var flags = NCWoundTreatmentFlags.None;
        if (medical.Wounds != null)
        {
            foreach (var wound in medical.Wounds.Records.Values)
                flags |= wound.TreatmentFlags;
        }

        if (medical.Treatment != null)
        {
            foreach (var treatment in medical.Treatment.WoundTreatments.Values)
                flags |= treatment.AppliedFlags;
        }

        return (NCCityTreatmentState) (ushort) flags;
    }

    private static NCCityBleedingState ClassifyBleeding(
        NCBleedingStateSnapshot? bleeding,
        float threshold)
    {
        if (bleeding == null)
            return NCCityBleedingState.None;

        var external = bleeding.OverflowExternalRate > threshold;
        var internalBleeding = bleeding.OverflowInternalRate > threshold ||
                               bleeding.OverflowInternalRatesByRegion.Values.Any(rate => rate > threshold);
        var controlled = false;
        var arterial = false;
        foreach (var source in bleeding.Sources)
        {
            if (source.CurrentRate > threshold)
            {
                if (source.Channel == NCBleedingChannel.Internal)
                    internalBleeding = true;
                else
                    external = true;
                arterial |= source.Channel == NCBleedingChannel.ExternalArterial;
            }
            else if (source.BaseRate > threshold &&
                     (source.PressureApplied || source.Bandaged || source.Clamped ||
                      source.Tourniqueted || source.Cauterized))
            {
                controlled = true;
            }
        }

        if (arterial)
            return NCCityBleedingState.Arterial;
        if (external && internalBleeding)
            return NCCityBleedingState.Combined;
        if (external)
            return NCCityBleedingState.External;
        if (internalBleeding)
            return NCCityBleedingState.Internal;
        return controlled ? NCCityBleedingState.Controlled : NCCityBleedingState.None;
    }

    private static NCCityCyberwareState ClassifyCyberware(
        NCMedicalStateSnapshot medical,
        NCCityActorSummaryPolicyPrototype policy)
    {
        var modules = new List<NCCyberwareStateSnapshot>(medical.CyberwareModules);
        foreach (var organ in medical.Organs.Values)
        {
            if (organ.Cyberware != null)
                modules.Add(organ.Cyberware);
        }

        if (modules.Count == 0)
            return NCCityCyberwareState.None;

        var state = NCCityCyberwareState.Operational;
        foreach (var module in modules)
        {
            var ratio = module.MaximumIntegrity > 0f
                ? module.CurrentIntegrity / module.MaximumIntegrity
                : 0f;
            if (!float.IsFinite(ratio) || ratio <= policy.FailedCyberwareIntegrityRatio ||
                module.EmpDisabledUntil > medical.CapturedAt ||
                module.NeuralState is NCCyberwareNeuralState.Disconnected or NCCyberwareNeuralState.Disrupted)
            {
                return NCCityCyberwareState.Disabled;
            }

            if (ratio < policy.DegradedCyberwareIntegrityRatio || !module.Calibrated ||
                module.NeuralState == NCCyberwareNeuralState.Degraded)
            {
                state = NCCityCyberwareState.Degraded;
            }
        }

        return state;
    }
}
