// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.CityServices;

[Serializable, NetSerializable]
public enum NCFactionMembershipStatus : byte
{
    Active,
    Suspended,
    Former,
}

public sealed record NCFactionMembership(
    NCOrganizationId FactionId,
    string Role,
    NCFactionMembershipStatus Status,
    NCActiveWorldTime JoinedAt,
    NCActiveWorldTime ChangedAt);

/// <summary>
/// Independent affiliations for one persistent actor. Employment is deliberately stored in another aggregate, so a
/// worker may also belong to a gang without corrupting either relationship.
/// </summary>
public sealed record NCActorFactionMembershipSnapshot(
    NCActorId ActorId,
    IReadOnlyList<NCFactionMembership> Memberships,
    int DataVersion,
    Guid LastAppliedEventId);
