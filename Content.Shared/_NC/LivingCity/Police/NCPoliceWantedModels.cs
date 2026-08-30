// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Police;

[Serializable, NetSerializable]
public readonly record struct NCPoliceWantedId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCPoliceWantedStatus : byte
{
    Active,
    Detained,
    Cleared,
    Cancelled,
}

/// <summary>Canonical cross-party wanted record shared by persistent actors and player characters.</summary>
public sealed record NCPoliceWantedSnapshot(
    NCPoliceWantedId WantedId,
    NCPartyRef Subject,
    string SubjectName,
    NCCityIncidentId IncidentId,
    Guid SourceWorldEventId,
    NCPoliceWantedStatus Status,
    int Risk,
    string ReasonKey,
    NCPartyRef IssuedBy,
    string IssuedByName,
    NCActiveWorldTime IssuedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);
