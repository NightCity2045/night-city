// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>
/// A reagent a laboratory can build a new compound from. It says what the reagent leans towards, how strongly, and
/// how much harm comes with it. A compound is invented by combining two or three of these: the result follows from
/// the ingredients alone, so a recipe that works once always works, and a researcher can learn which ingredients
/// go together.
/// </summary>
[Prototype("ncPharmaIngredient")]
public sealed partial class NCPharmaIngredientPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>The reagent that goes into the beaker.</summary>
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Reagent { get; private set; }

    /// <summary>What the ingredient does: one of Analgesic, Sedative, Stimulant, Antibiotic, Immunosuppressant, Antitoxin. Empty for a neutraliser.</summary>
    [DataField]
    public string Category { get; private set; } = string.Empty;

    /// <summary>How strongly it pushes the compound towards its category, 0 to 1.</summary>
    [DataField]
    public float Potency { get; private set; }

    /// <summary>How much harm it adds, 0 to 1. A negative value neutralises harm.</summary>
    [DataField]
    public float Toxicity { get; private set; }

    /// <summary>A side effect it brings: rhythm, motor, respiratory or cardiac. Empty for none.</summary>
    [DataField]
    public string Side { get; private set; } = string.Empty;

    /// <summary>The dependence it leaves behind, if any.</summary>
    [DataField]
    public string? Dependence { get; private set; }
}
