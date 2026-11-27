// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A data chip that holds what a laboratory has studied. The research console shows only the nodes of the formula
/// tree that are on the chip in its slot: take the chip out and everything is unstudied again, put another in and
/// its record is what the console knows. A new chip is blank, and the research is done again.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaRdSystem))]
public sealed partial class NCPharmaResearchChipComponent : Component
{
    /// <summary>The nodes of the tree this chip has studied, as node ids.</summary>
    [DataField]
    public HashSet<string> Researched = new();

    /// <summary>A chip that comes with the research of the stock formulas already on it.</summary>
    [DataField]
    public bool Stock;
}

/// <summary>
/// Raised when a laboratory learns something about nodes of the formula tree. The chips in the consoles of that
/// laboratory record it: those near the machine where the work was done. Without a site (an administrator's edit) every
/// chip in a console records it.
/// </summary>
[ByRefEvent]
public readonly record struct NCPharmaResearchEvent(string[] Nodes, EntityUid? Site = null);
