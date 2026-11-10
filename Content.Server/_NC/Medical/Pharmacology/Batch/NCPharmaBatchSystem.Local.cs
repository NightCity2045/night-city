// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

public sealed partial class NCPharmaBatchSystem
{
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    /// <summary>
    /// Infuses blood-like volume through the same transfusion rules as donor blood. A synthetic blood is
    /// universal, so it uses a group that every recipient accepts; the volume is bounded by the same limits.
    /// </summary>
    private void InfuseVolume(EntityUid patient, float volume)
    {
        if (!_prototypes.TryIndex<Content.Shared._NC.Medical.Emergency.NCEmergencyCareProfilePrototype>("NCHumanEmergencyCare", out var care))
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
    /// (scaled by strain sensitivity), an analgesic numbs and a procoagulant slows bleeding for a while. All of
    /// it depends on the blood supply that carries the drug to the tissue.
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
        if (analgesia > 0f || hemostasis > 0f)
            _pharmacology.SetLocalEffect(patient, analgesia, hemostasis, tuning.LocalDuration);

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
