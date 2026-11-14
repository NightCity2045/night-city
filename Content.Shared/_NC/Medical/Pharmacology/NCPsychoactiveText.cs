// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>Localization ids for the felt phases, so no sign is ever built from a raw enum name.</summary>
public static class NCPsychoactiveText
{
    public static string PhaseKey(NCDrugPhase phase) => phase switch
    {
        NCDrugPhase.Onset => "nc-pharma-phase-onset",
        NCDrugPhase.Peak => "nc-pharma-phase-peak",
        NCDrugPhase.Decline => "nc-pharma-phase-decline",
        NCDrugPhase.Comedown => "nc-pharma-phase-comedown",
        _ => "nc-pharma-phase-none",
    };

    /// <summary>Selector value used inside the Fluent messages of the phases.</summary>
    public static string EffectId(NCPsychoactiveEffect effect) => effect switch
    {
        NCPsychoactiveEffect.Sedation => "sedation",
        NCPsychoactiveEffect.Euphoria => "euphoria",
        NCPsychoactiveEffect.Perception => "perception",
        NCPsychoactiveEffect.Sociability => "sociability",
        NCPsychoactiveEffect.Dissociation => "dissociation",
        _ => "stimulation",
    };
}
