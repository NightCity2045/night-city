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
    /// The part of a dose that works directly on the wounds. Only an antibiotic has a local action: it
    /// removes infection from every wound, scaled by the blood supply that brings it there and by how well it
    /// works on this patient's strain. Anything else applied locally is simply not absorbed by the tissue.
    /// </summary>
    private void ApplyLocalTissue(
        EntityUid patient,
        ProtoId<NCDrugProfilePrototype> compound,
        float localConcentration,
        NCPharmaTuningPrototype tuning)
    {
        if (!_prototypes.TryIndex(compound, out var drug) || drug.AntibioticPowerAtTherapeutic <= 0f ||
            !TryComp<NCWoundContainerComponent>(patient, out var wounds) || wounds.Wounds.Count == 0)
        {
            return;
        }

        var perfusion = TryComp<NCCirculationComponent>(patient, out var circulation) ? circulation.Perfusion : 1f;
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
