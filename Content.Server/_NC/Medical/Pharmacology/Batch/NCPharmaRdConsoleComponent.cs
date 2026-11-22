// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A pharmacologist's console for formula development. It holds no formulas itself: they live in the batch
/// registry, so every console and every production station sees the same set.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaRdSystem))]
public sealed partial class NCPharmaRdConsoleComponent : Component
{
    /// <summary>Formula whose notebook is shown. It follows the researcher's selection.</summary>
    [ViewVariables]
    public string Selected = string.Empty;

    /// <summary>The slot that holds the beaker with the reagents a new compound is made from.</summary>
    [DataField]
    public string ReagentSlot = "reagents";

    /// <summary>The slot that holds the data chip with the research record.</summary>
    [DataField]
    public string ChipSlot = "chip";

    /// <summary>Units of each ingredient one invention uses up.</summary>
    [DataField]
    public float UnitsPerIngredient = 5f;
}
