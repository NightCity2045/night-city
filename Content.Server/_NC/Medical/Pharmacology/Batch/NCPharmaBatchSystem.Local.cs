// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

public sealed partial class NCPharmaBatchSystem
{
    private static readonly ProtoId<Content.Shared._NC.Medical.Emergency.NCEmergencyCareProfilePrototype> EmergencyCareProfile = "NCHumanEmergencyCare";

    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    /// <summary>
    /// Infuses blood-like volume through the same transfusion rules as donor blood. A synthetic blood is
    /// universal, so it uses a group that every recipient accepts; the volume is bounded by the same limits.
    /// </summary>
    private void InfuseVolume(EntityUid patient, float volume)
    {
        if (!_prototypes.TryIndex(EmergencyCareProfile, out var care))
            return;

        _blood.TryTransfuse(
            patient,
            "NC-Synthetic",
            volume,
            care.MaximumBloodVolumeMultiplier,
            care.TransfusionCompatibility);
    }

    /// <summary>
    /// The part of a dose that works directly on the wounds: an antibiotic removes infection from every wound
    /// (scaled by strain sensitivity), an analgesic numbs the most painful wound and a procoagulant slows the
    /// hardest-bleeding one, for a while. All of it depends on the blood supply that carries the drug there.
    /// </summary>
    private void ApplyLocalTissue(
        EntityUid patient,
        ProtoId<NCDrugProfilePrototype> compound,
        float localConcentration,
        NCPharmaTuningPrototype tuning)
    {
        if (!_prototypes.TryIndex(compound, out var drug) ||
            !TryComp<NCWoundContainerComponent>(patient, out var wounds) || wounds.Wounds.Count == 0)
        {
            return;
        }

        var perfusion = TryComp<NCCirculationComponent>(patient, out var circulation) ? circulation.Perfusion : 1f;
        var (analgesia, hemostasis) = NCPharmaBatchMath.ResolveLocalEffects(
            tuning, drug.AnalgesiaAtTherapeutic, drug.BleedingMultiplierAtTherapeutic,
            localConcentration, drug.TherapeuticConcentration, perfusion);
        // The dose is applied to the wound that needs it most: the one bleeding hardest and the one that hurts most.
        if (hemostasis > 0f)
        {
            var bleeding = wounds.Wounds.Values
                .Where(wound => wound.ClosureState == NCWoundClosureState.Open)
                .OrderByDescending(wound => wound.ExternalBleedRate + wound.InternalBleedRate)
                .FirstOrDefault();
            if (bleeding != null)
            {
                bleeding.LocalHemostasis = MathF.Max(bleeding.LocalHemostasis, hemostasis);
                bleeding.LocalRemaining = tuning.LocalDuration > bleeding.LocalRemaining ? tuning.LocalDuration : bleeding.LocalRemaining;
            }
        }

        if (analgesia > 0f)
        {
            var painful = wounds.Wounds.Values.OrderByDescending(wound => wound.Pain).FirstOrDefault();
            if (painful != null)
            {
                painful.LocalAnalgesia = MathF.Max(painful.LocalAnalgesia, analgesia);
                painful.LocalRemaining = tuning.LocalDuration > painful.LocalRemaining ? tuning.LocalDuration : painful.LocalRemaining;
            }
        }

        var relief = NCPharmaBatchMath.ResolveLocalInfectionRelief(
            tuning,
            drug.AntibioticPowerAtTherapeutic,
            _pharmacology.GetSusceptibility(patient, compound),
            localConcentration,
            drug.TherapeuticConcentration,
            perfusion);
        if (relief <= 0f)
            return;

        foreach (var wound in wounds.Wounds.Values)
            wound.Infection = Math.Clamp(wound.Infection - relief, 0f, 1f);

        _medicalBodies.TryMarkDirty(patient, NCMedicalDirtyDomains.Wounds | NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence);
    }
}
