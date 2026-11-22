// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

[Serializable, NetSerializable]
public enum NCPharmaRdUiKey : byte
{
    Key,
}

/// <summary>Everything the research console shows: known formulas, the notebook and the last answer.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdState(
    NCPharmaFormulaSummary[] formulas,
    NCPharmaJournalSummary[] journal,
    NCPharmaRdNotice notice,
    string[] cores,
    NCPharmaNodeKnowledge[] knowledge,
    bool hasBeaker,
    NCPharmaReagentAmount[] beaker) : BoundUserInterfaceState
{
    public readonly NCPharmaFormulaSummary[] Formulas = formulas;
    public readonly NCPharmaJournalSummary[] Journal = journal;
    public readonly NCPharmaRdNotice Notice = notice;

    /// <summary>Compounds the laboratory may build a formula on: identified ones that some formula already uses.</summary>
    public readonly string[] Cores = cores;

    /// <summary>What the laboratory knows about each node. Anything not listed is unknown.</summary>
    public readonly NCPharmaNodeKnowledge[] Knowledge = knowledge;

    /// <summary>Whether a beaker stands in the console's reagent slot.</summary>
    public readonly bool HasBeaker = hasBeaker;

    /// <summary>What the beaker holds. Inventing a compound spends ingredients from it.</summary>
    public readonly NCPharmaReagentAmount[] Beaker = beaker;
}

/// <summary>
/// Invents a compound from two or three ingredient reagents in the console's beaker. The server checks that the
/// beaker holds enough of each, spends it and returns the new compound as an experimental formula.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdInventMessage(string[] ingredients) : BoundUserInterfaceMessage
{
    public readonly string[] Ingredients = ingredients;
}

/// <summary>
/// Builds a formula from the tree: one or two cores, a delivery form, a concentration, up to two modifiers and
/// a production mode. The parent only says which line the result continues; the name is never typed by the
/// player. The server revalidates every field.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdComposeMessage(
    string? parent,
    string firstCore,
    string? secondCore,
    string deliveryForm,
    float concentration,
    string[] modifiers,
    string profile) : BoundUserInterfaceMessage
{
    public readonly string? Parent = parent;
    public readonly string FirstCore = firstCore;
    public readonly string? SecondCore = secondCore;
    public readonly string DeliveryForm = deliveryForm;
    public readonly float Concentration = concentration;
    public readonly string[] Modifiers = modifiers;
    public readonly string Profile = profile;
}

/// <summary>Moves a verified experimental version to regular production.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdApproveMessage(string formula) : BoundUserInterfaceMessage
{
    public readonly string Formula = formula;
}

/// <summary>Asks for the notebook of one formula's lineage.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdSelectMessage(string formula) : BoundUserInterfaceMessage
{
    public readonly string Formula = formula;
}

/// <summary>A researcher's own note on a formula version: the hypothesis, what was changed, what was concluded.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdNoteMessage(string formula, string text) : BoundUserInterfaceMessage
{
    public readonly string Formula = formula;
    public readonly string Text = text;
}
