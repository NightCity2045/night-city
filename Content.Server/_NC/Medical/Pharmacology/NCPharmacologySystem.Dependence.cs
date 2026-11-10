// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Pharmacology;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology;

/// <summary>
/// Tolerance, dependence and withdrawal. Withdrawal reuses the ordinary drug physiology: it is a symptom
/// compound kept at a level the dependence and the time since the last dose call for, so it needs no special
/// treatment in any other system and is cleared the same way as every other exposure.
/// </summary>
public sealed partial class NCPharmacologySystem
{
    /// <summary>Smallest missing amount of a symptom compound worth topping up.</summary>
    private const float WithdrawalTopUpEpsilon = 0.01f;

    private void RegisterDependenceDose(NCPharmacologyComponent state, NCDrugProfilePrototype drug, float concentration)
    {
        if (drug.Dependence is not { } dependenceId ||
            !_prototypes.TryIndex(dependenceId, out NCDependenceProfilePrototype? dependence) ||
            !NCDependenceMath.Validate(dependence))
        {
            return;
        }

        if (!state.Dependence.TryGetValue(drug.ID, out var record))
        {
            if (state.Dependence.Count >= AbsoluteDrugCapacityLimit)
                return;
            record = new NCDrugDependenceState();
            state.Dependence.Add(drug.ID, record);
        }

        NCDependenceMath.RegisterDose(dependence, record, concentration, _timing.CurTime);
    }

    private float ResolveToleranceFactor(
        NCPharmacologyComponent state,
        ProtoId<NCDrugProfilePrototype> drugId,
        NCDrugProfilePrototype drug)
    {
        if (drug.Dependence is not { } dependenceId ||
            !state.Dependence.TryGetValue(drugId, out var record) ||
            !_prototypes.TryIndex(dependenceId, out NCDependenceProfilePrototype? dependence))
        {
            return 1f;
        }

        return NCDependenceMath.EffectFactor(dependence, record);
    }

    /// <summary>
    /// Fades tolerance and dependence, builds craving and keeps the withdrawal symptom compound at its
    /// current target. Returns true when anything about the body changed.
    /// </summary>
    private bool AdvanceDependence(EntityUid body, NCPharmacologyComponent state, float seconds)
    {
        if (state.Dependence.Count == 0)
            return false;

        var changed = false;
        var now = _timing.CurTime;
        var removal = new List<ProtoId<NCDrugProfilePrototype>>();
        foreach (var drugId in state.Dependence.Keys.OrderBy(key => key.Id, StringComparer.Ordinal).ToList())
        {
            var record = state.Dependence[drugId];
            if (!_prototypes.TryIndex(drugId, out NCDrugProfilePrototype? drug) ||
                drug.Dependence is not { } dependenceId ||
                !_prototypes.TryIndex(dependenceId, out NCDependenceProfilePrototype? dependence) ||
                !NCDependenceMath.Validate(dependence))
            {
                removal.Add(drugId);
                continue;
            }

            var compoundActive = state.ActiveDrugs.TryGetValue(drugId, out var exposure) &&
                                 exposure.Concentration >= drug.MinimumEffectiveConcentration;
            var replacementActive = IsReplacementActive(state, dependence);
            var previousStage = record.Stage;
            var previousDependence = record.Dependence;

            NCDependenceMath.Decay(dependence, record, seconds, replacementActive);
            NCDependenceMath.AdvanceCraving(
                dependence,
                record,
                seconds,
                record.Dependence >= dependence.WithdrawalThreshold,
                compoundActive);
            var outcome = NCDependenceMath.ResolveWithdrawal(dependence, record, now, compoundActive, replacementActive);
            record.Stage = outcome.Stage == NCWithdrawalStage.None && record.Craving > 0.2f
                ? NCWithdrawalStage.Craving
                : outcome.Stage;
            changed |= !MathHelper.CloseTo(previousDependence, record.Dependence) || previousStage != record.Stage;

            if (outcome.SymptomConcentration > 0f && dependence.WithdrawalCompound is { } symptom)
                changed |= TopUpWithdrawal(body, state, symptom, outcome.SymptomConcentration);

            if (record.Tolerance <= 0f && record.Dependence <= 0f && record.Craving <= 0f &&
                outcome.Stage == NCWithdrawalStage.None)
            {
                removal.Add(drugId);
            }
        }

        foreach (var drugId in removal)
        {
            state.Dependence.Remove(drugId);
            changed = true;
        }

        return changed;
    }

    private bool IsReplacementActive(NCPharmacologyComponent state, NCDependenceProfilePrototype dependence)
    {
        foreach (var replacementId in dependence.ReplacementDrugs)
        {
            if (state.ActiveDrugs.TryGetValue(replacementId, out var exposure) &&
                _prototypes.TryIndex(replacementId, out NCDrugProfilePrototype? replacement) &&
                exposure.Concentration >= replacement.MinimumEffectiveConcentration)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Raises the symptom compound to its target without ever lowering it, so withdrawal only grows.</summary>
    private bool TopUpWithdrawal(
        EntityUid body,
        NCPharmacologyComponent state,
        ProtoId<NCDrugProfilePrototype> symptom,
        float target)
    {
        var current = state.ActiveDrugs.TryGetValue(symptom, out var exposure) ? exposure.Concentration : 0f;
        var missing = target - current;
        if (missing <= WithdrawalTopUpEpsilon)
            return false;

        return ApplyDose(body, symptom, missing).Accepted;
    }

    /// <summary>
    /// Reads the withdrawal stage of one compound for diagnostics and admin tools. It never exposes the raw
    /// dependence value.
    /// </summary>
    public NCWithdrawalStage GetWithdrawalStage(EntityUid body, ProtoId<NCDrugProfilePrototype> drug) =>
        TryComp<NCPharmacologyComponent>(body, out var state) &&
        state.Dependence.TryGetValue(drug, out var record)
            ? record.Stage
            : NCWithdrawalStage.None;

    private bool TryCaptureDependence(
        NCPharmacologyComponent state,
        TimeSpan timestamp,
        NCPharmacologyStateSnapshot captured)
    {
        foreach (var (drugId, record) in state.Dependence.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
        {
            if (!NCDependenceMath.Validate(record) ||
                !_prototypes.HasIndex(drugId) ||
                record.LastDose < TimeSpan.Zero ||
                record.LastDose > timestamp)
            {
                return false;
            }

            captured.Dependence.Add(new NCDrugDependenceSnapshot
            {
                Profile = drugId,
                Tolerance = record.Tolerance,
                Dependence = record.Dependence,
                Craving = record.Craving,
                TimeSinceLastDose = timestamp - record.LastDose,
                Stage = record.Stage,
            });
        }

        return true;
    }

    private void RestoreDependence(
        NCPharmacologyComponent state,
        NCPharmacologyStateSnapshot snapshot,
        TimeSpan timestamp)
    {
        state.Dependence.Clear();
        foreach (var record in snapshot.Dependence)
        {
            state.Dependence[record.Profile] = new NCDrugDependenceState
            {
                Tolerance = record.Tolerance,
                Dependence = record.Dependence,
                Craving = record.Craving,
                LastDose = timestamp >= record.TimeSinceLastDose ? timestamp - record.TimeSinceLastDose : TimeSpan.Zero,
                Stage = record.Stage,
            };
        }
    }

    private bool ValidateDependenceSnapshot(NCPharmacologyStateSnapshot snapshot)
    {
        if (snapshot.Dependence == null || snapshot.Dependence.Count > AbsoluteDrugCapacityLimit)
            return false;

        var seen = new HashSet<ProtoId<NCDrugProfilePrototype>>();
        foreach (var record in snapshot.Dependence)
        {
            if (record == null ||
                !seen.Add(record.Profile) ||
                !_prototypes.HasIndex(record.Profile) ||
                record.TimeSinceLastDose < TimeSpan.Zero ||
                !NCDependenceMath.Validate(new NCDrugDependenceState
                {
                    Tolerance = record.Tolerance,
                    Dependence = record.Dependence,
                    Craving = record.Craving,
                    Stage = record.Stage,
                }))
            {
                return false;
            }
        }

        return true;
    }
}
