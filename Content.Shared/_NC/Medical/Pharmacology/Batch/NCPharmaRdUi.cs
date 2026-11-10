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
    NCPharmaRdNotice notice) : BoundUserInterfaceState
{
    public readonly NCPharmaFormulaSummary[] Formulas = formulas;
    public readonly NCPharmaJournalSummary[] Journal = journal;
    public readonly NCPharmaRdNotice Notice = notice;
}

/// <summary>Develops a new experimental version of a formula. The server revalidates every field.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaRdDeriveMessage(
    string parent,
    string name,
    string deliveryForm,
    float concentration,
    string[] modifiers) : BoundUserInterfaceMessage
{
    public readonly string Parent = parent;
    public readonly string Name = name;
    public readonly string DeliveryForm = deliveryForm;
    public readonly float Concentration = concentration;
    public readonly string[] Modifiers = modifiers;
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
