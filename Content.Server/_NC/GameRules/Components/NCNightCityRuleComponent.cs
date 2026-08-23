// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.GameRules.Components;

/// <summary>
/// Marks the authoritative rule entity for a Night City round. Domain state remains owned by the
/// Living City systems that follow the normal <c>InRound</c> lifecycle.
/// </summary>
[RegisterComponent, Access(typeof(NCNightCityRuleSystem))]
public sealed partial class NCNightCityRuleComponent : Component;
