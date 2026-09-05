// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors.Components;

public enum NCHumanDirectiveExecutionPhase : byte
{
    Accepted,
    Approaching,
    Pulling,
    Traveling,
    Active,
}

/// <summary>Physical projection of the latest authorized round-local directive.</summary>
[RegisterComponent]
public sealed partial class NCHumanDirectiveComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadOnly)] public Guid RequestId;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCActorId IssuedTo;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCCityCommitmentId CommitmentId;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public string Action = string.Empty;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public string Order = string.Empty;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCZoneId TargetZone;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime AcceptedAt;
    [DataField, ViewVariables(VVAccess.ReadOnly)] public NCActiveWorldTime ExpiresAt;
    [ViewVariables(VVAccess.ReadOnly)] public EntityUid? TargetEntity;
    [ViewVariables(VVAccess.ReadOnly)] public string TargetHandle = string.Empty;
    [ViewVariables(VVAccess.ReadOnly)] public NCHumanDirectiveExecutionPhase Phase;
}
