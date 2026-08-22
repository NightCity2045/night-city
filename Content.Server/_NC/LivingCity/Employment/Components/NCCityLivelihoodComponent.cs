// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Employment;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment.Components;

/// <summary>
/// Physical projection of an abstract resident livelihood. The persistent registry remains authoritative.
/// </summary>
[RegisterComponent]
public sealed partial class NCCityLivelihoodComponent : Component
{
    [DataField]
    public ProtoId<NCLivelihoodProfilePrototype> Profile { get; set; }
}
