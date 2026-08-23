// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors.Components;

/// <summary>Round-local projection of the latest authorized persistent commitment.</summary>
[RegisterComponent]
public sealed partial class NCHumanDirectiveComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadOnly)] public Guid RequestId;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCActorId IssuedTo;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public string Action = string.Empty;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public string Order = string.Empty;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCZoneId TargetZone;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime AcceptedAt;
}
