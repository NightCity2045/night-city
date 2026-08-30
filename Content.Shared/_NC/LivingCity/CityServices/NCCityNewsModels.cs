// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.CityServices;

[Serializable, NetSerializable]
public readonly record struct NCCityNewsId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCCityNewsKind : byte { Alert, Incident, ServiceNotice, Investigation, Resolution }

public sealed record NCCityNewsSnapshot(NCCityNewsId NewsId, NCCityIncidentId IncidentId, NCCityNewsKind Kind,
    NCZoneId ZoneId, int Severity, string HeadlineKey, string BodyKey, IReadOnlyList<string> Tags,
    Guid SourceWorldEventId, NCActiveWorldTime PublishedAt, bool Retracted, int DataVersion,
    Guid LastAppliedEventId);
