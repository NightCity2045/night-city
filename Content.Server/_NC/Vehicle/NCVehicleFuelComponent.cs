// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Server bridge to real chemistry reservoirs; fractional consumption is carried, never rounded away.</summary>
[RegisterComponent]
public sealed partial class NCVehicleFuelComponent : Component
{
    [DataField] public string Solution = "fuel";
    [DataField(required: true)] public ProtoId<ReagentPrototype> Reagent;
    [DataField] public float UnitsPerLitre = 1000;
    [ViewVariables] public float PendingLitres;
}
