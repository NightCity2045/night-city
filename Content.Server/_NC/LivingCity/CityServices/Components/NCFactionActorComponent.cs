// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices.Components;

/// <summary>Round-local projection of a persistent faction membership onto the common physical human.</summary>
[RegisterComponent]
public sealed partial class NCFactionActorComponent : Component
{
    [DataField] public NCActorId ActorId;
    [DataField] public NCOrganizationId FactionId;
    [DataField] public ProtoId<NCFactionProfilePrototype> Profile;
    [DataField] public string Role = string.Empty;
}
