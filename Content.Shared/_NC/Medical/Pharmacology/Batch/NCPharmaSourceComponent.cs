// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Marks a canister as source material for a synthesis run. The material itself is the canister's solution: the
/// reagent of a feedstock is the wanted compound, a neutral impurity dilutes it and sludge contaminates it. Purity,
/// contamination and quantity are read from that composition, so a canister can be poured, mixed and topped up like
/// any other container.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCPharmaSourceComponent : Component;
