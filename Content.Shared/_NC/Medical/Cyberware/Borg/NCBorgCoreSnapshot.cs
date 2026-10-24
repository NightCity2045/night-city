// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware.Borg;

/// <summary>Semantic physical carrier state. UI sessions and runtime body/entity references are absent.</summary>
[DataDefinition]
public sealed partial class NCBorgCoreSnapshot
{
    [DataField] public Guid CoreId;
    [DataField] public bool Occupied;
    [DataField] public float Temperature;
    [DataField] public TimeSpan EmpRemaining;
    [DataField] public NCCyberwarePowerBusSnapshot PowerBus = new();
    [DataField] public Solution Support = new();
    [DataField] public Solution Bloodstream = new();
    [DataField] public Solution Metabolites = new();
    [DataField] public Solution Stomach = new();
    [DataField] public Dictionary<ProtoId<ReagentPrototype>, float> ConsumptionRemainders = [];
}
