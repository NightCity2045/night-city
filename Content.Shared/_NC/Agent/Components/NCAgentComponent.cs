// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Agent.Components;

/// <summary>
/// Marks a PDA as the Night City Agent while keeping all PDA and ID-card mechanics intact. The names of the Agent and of its
/// VIN card live in their prototypes (<c>NCAgent</c>, <c>NCVinCard</c>) and their localization, not in code.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCAgentComponent : Component;
