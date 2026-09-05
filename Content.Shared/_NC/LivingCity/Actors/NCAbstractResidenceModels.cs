// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Stable identity of a coordinate-free home. It is never an EntityUid, map entity, room, or property identifier.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCAbstractResidenceId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCAbstractResidenceKind : byte
{
    Squat,
    Cube,
    SharedApartment,
    Apartment,
}

[Serializable, NetSerializable]
public enum NCAbstractResidenceQuality : byte
{
    Precarious,
    Basic,
    Standard,
    Comfortable,
}

/// <summary>
/// Durable semantic home assigned to one resident. District and local zone are authored geography only; the record
/// deliberately contains no coordinates, room IDs, portal IDs, physical apartment IDs, or ECS references.
/// </summary>
public sealed record NCCityAbstractResidence(
    NCAbstractResidenceId ResidenceId,
    NCZoneId DistrictZone,
    NCZoneId LocalZone,
    string ProfileId,
    NCAbstractResidenceKind Kind,
    NCAbstractResidenceQuality Quality,
    int WeeklyCost);
