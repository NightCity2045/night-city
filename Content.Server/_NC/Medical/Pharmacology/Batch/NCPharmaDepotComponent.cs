// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Content.Shared._NC.Medical.Pharmacology;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Doses that have been administered but are still being absorbed. It exists only while something is arriving
/// and is removed when the depot is empty, so a healthy body carries no pharmaceutical state at all.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCPharmaDepotSystem))]
public sealed partial class NCPharmaDepotComponent : Component
{
    [ViewVariables]
    public readonly List<NCPharmaDepotEntry> Entries = [];
}

/// <summary>Remaining concentration of one dose and how fast it is arriving.</summary>
public sealed class NCPharmaDepotEntry
{
    public ProtoId<NCDrugProfilePrototype> Drug;
    public float Remaining;
    public float RatePerSecond;
    public float ClearanceFactor = 1f;
}
