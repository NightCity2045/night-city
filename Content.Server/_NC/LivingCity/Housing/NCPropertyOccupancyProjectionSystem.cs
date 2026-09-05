// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using Content.Server._NC.Housing;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;

namespace Content.Server._NC.LivingCity.Housing;

public sealed record NCPropertyOccupancyProjection(
    NCApartmentId PropertyId,
    NCZoneId[] ZoneIds,
    NCPropertyStatus Status,
    NCPropertyLegalState LegalState,
    int ResidentCount,
    bool IsVacant,
    long AggregateVersion);

/// <summary>
/// Event-fed, entity-free housing projection for A-Life population and vacancy decisions. Consumers query mapper zone
/// IDs and never scan apartment entities or persistent storage on a simulation tick.
/// </summary>
public sealed partial class NCPropertyOccupancyProjectionSystem : EntitySystem
{
    [Dependency] private NCPropertyRegistrySystem _properties = default!;

    private readonly ConcurrentDictionary<NCApartmentId, NCPropertyOccupancyProjection> _projection = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPropertyAggregatePersistedEvent>(OnPropertyPersisted);
        SubscribeLocalEvent<NCPropertyChangedEvent>(OnRuntimePropertyChanged);
    }

    public bool TryGet(NCApartmentId propertyId, out NCPropertyOccupancyProjection projection) =>
        _projection.TryGetValue(propertyId, out projection!);

    public IReadOnlyList<NCPropertyOccupancyProjection> GetForZone(NCZoneId zoneId, int limit)
    {
        if (!zoneId.IsValid || limit <= 0)
            return [];
        return _projection.Values
            .Where(value => value.PropertyId.ZoneId == zoneId || value.ZoneIds.Contains(zoneId))
            .OrderBy(value => value.PropertyId.Value)
            .Take(Math.Min(limit, 256))
            .ToArray();
    }

    private void OnPropertyPersisted(NCPropertyAggregatePersistedEvent args) => Cache(args.Property);

    private void OnRuntimePropertyChanged(ref NCPropertyChangedEvent args) => _ = RefreshAsync(args.PropertyId);

    private async Task RefreshAsync(NCApartmentId propertyId)
    {
        var property = await _properties.GetAsync(propertyId, true);
        if (property != null)
            Cache(property);
    }

    private void Cache(NCPropertyAggregateRecord property)
    {
        var current = property.Snapshot;
        _projection.AddOrUpdate(current.PropertyId,
            _ => Create(property),
            (_, previous) => previous.AggregateVersion >= property.AggregateVersion ? previous : Create(property));
    }

    private static NCPropertyOccupancyProjection Create(NCPropertyAggregateRecord property)
    {
        var current = property.Snapshot;
        var residents = current.Occupants.Count(value => value.Kind is NCPropertyOccupancyKind.Ownership or
            NCPropertyOccupancyKind.Tenancy or NCPropertyOccupancyKind.Resident);
        return new NCPropertyOccupancyProjection(current.PropertyId, current.RoomIds, current.Status,
            current.LegalState, residents,
            residents == 0 && current.Status == NCPropertyStatus.Available &&
            current.LegalState == NCPropertyLegalState.Clear,
            property.AggregateVersion);
    }
}
