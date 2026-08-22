// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Population;

public enum NCCityResidenceAllocationStatus : byte
{
    Completed,
    Partial,
    AlreadyRunning,
    Rejected,
}

public sealed record NCCityResidenceAllocationReport(
    NCCityResidenceAllocationStatus Status,
    int ScannedActors,
    int ApartmentZones,
    int DistrictsWithApartments,
    int ExistingPhysicalResidents,
    int AssignedPhysicalResidents,
    int AbstractResidents,
    int UnresolvedResidents,
    int FailedMutations,
    string? Error);

/// <summary>
/// Explicitly assigns a data-driven minority of district residents to real apartment zones. It never creates map
/// entities, fake zone geometry, or apartments beyond what the mapper authored.
/// </summary>
public sealed partial class NCCityResidenceAllocationSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultResidenceAllocationPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;

    private readonly List<NCZoneInfo> _zoneBuffer = [];
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public NCCityResidenceAllocationReport? LastReport { get; private set; }

    public async Task<NCCityResidenceAllocationReport> AllocateAsync(string auditSource)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Report(NCCityResidenceAllocationStatus.AlreadyRunning, error: "residence-allocation-already-running");

        try
        {
            if (!_clock.TryGetCurrentTime(out _, out _) ||
                !_prototypes.TryIndex(
                    new ProtoId<NCCityResidenceAllocationPolicyPrototype>(DefaultPolicyId),
                    out NCCityResidenceAllocationPolicyPrototype? policy) || policy == null ||
                !ValidPolicy(policy) || string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > 128)
            {
                return LastReport = Report(NCCityResidenceAllocationStatus.Rejected,
                    error: "active-round-or-residence-policy-invalid");
            }

            _zones.GetZones(_zoneBuffer);
            var apartmentsByDistrict = new Dictionary<NCZoneId, List<NCZoneId>>();
            foreach (var zone in _zoneBuffer)
            {
                if (zone.ContextSlot != NCZoneContextSlot.Apartment ||
                    !TryResolveDistrict(zone.Id, out var district))
                {
                    continue;
                }

                if (!apartmentsByDistrict.TryGetValue(district, out var apartments))
                    apartmentsByDistrict[district] = apartments = [];
                apartments.Add(zone.Id);
            }
            foreach (var apartments in apartmentsByDistrict.Values)
                apartments.Sort(static (left, right) => left.Value.CompareTo(right.Value));

            var records = await ReadActors(policy);
            if (records == null)
            {
                return LastReport = Report(NCCityResidenceAllocationStatus.Rejected,
                    error: "actor-count-exceeds-residence-policy");
            }

            var abstractByDistrict = new Dictionary<NCZoneId, List<NCCityActorRecord>>();
            var occupancy = new Dictionary<NCZoneId, int>();
            var existingPhysical = 0;
            var unresolved = 0;
            foreach (var actor in records)
            {
                var homeZone = actor.Snapshot.Location.HomeZone;
                if (!TryResolveDistrict(homeZone, out var district))
                {
                    unresolved++;
                    continue;
                }

                if (_zones.TryGetZone(homeZone, out var home) &&
                    home.ContextSlot == NCZoneContextSlot.Apartment)
                {
                    existingPhysical++;
                    occupancy[homeZone] = occupancy.GetValueOrDefault(homeZone) + 1;
                    continue;
                }

                if (!abstractByDistrict.TryGetValue(district, out var residents))
                    abstractByDistrict[district] = residents = [];
                residents.Add(actor);
            }

            var assignments = new List<(NCCityActorRecord Actor, NCZoneId Apartment)>();
            foreach (var (district, apartments) in apartmentsByDistrict.OrderBy(static pair => pair.Key.Value))
            {
                if (!abstractByDistrict.TryGetValue(district, out var residents) || residents.Count == 0)
                    continue;
                residents.Sort(static (left, right) =>
                    left.Snapshot.Identity.ActorId.Value.CompareTo(right.Snapshot.Identity.ActorId.Value));
                var eligibleResidents = residents
                    .Where(static actor => !actor.Snapshot.Lifecycle.Frozen &&
                        actor.Snapshot.Lifecycle.Status is not
                            (NCCityActorStatus.Dead or NCCityActorStatus.Archived))
                    .ToArray();

                var occupied = apartments.Sum(apartment => occupancy.GetValueOrDefault(apartment));
                var districtResidents = occupied + residents.Count;
                var capacity = checked(apartments.Count * policy.PhysicalResidentsPerApartment);
                var targetPhysical = Math.Min(capacity,
                    (int) Math.Ceiling(districtResidents * policy.PhysicalResidentShare));
                var required = Math.Min(eligibleResidents.Length, Math.Max(0, targetPhysical - occupied));
                for (var index = 0; index < required; index++)
                {
                    var apartment = apartments
                        .Where(value => occupancy.GetValueOrDefault(value) < policy.PhysicalResidentsPerApartment)
                        .OrderBy(value => occupancy.GetValueOrDefault(value))
                        .ThenBy(static value => value.Value)
                        .FirstOrDefault();
                    if (!apartment.IsValid)
                        break;
                    assignments.Add((eligibleResidents[index], apartment));
                    occupancy[apartment] = occupancy.GetValueOrDefault(apartment) + 1;
                }
            }

            var assigned = 0;
            var failed = 0;
            for (var offset = 0; offset < assignments.Count; offset += policy.MaximumConcurrentMutations)
            {
                var batch = assignments.Skip(offset).Take(policy.MaximumConcurrentMutations)
                    .Select(assignment => _actors.SetResidenceAsync(new NCCityActorResidenceMutationRequest(
                        Guid.NewGuid(),
                        assignment.Actor.Snapshot.Identity.ActorId,
                        assignment.Actor.AggregateVersion,
                        assignment.Apartment,
                        Guid.NewGuid(),
                        AuditSource: auditSource)))
                    .ToArray();
                var results = await Task.WhenAll(batch);
                assigned += results.Count(static result => result.Status is
                    NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate);
                failed += results.Count(static result => result.Status is not
                    (NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate));
            }

            var abstractResidents = records.Count - unresolved - existingPhysical - assigned;
            var status = failed == 0 ? NCCityResidenceAllocationStatus.Completed : NCCityResidenceAllocationStatus.Partial;
            return LastReport = new NCCityResidenceAllocationReport(
                status,
                records.Count,
                apartmentsByDistrict.Values.Sum(static list => list.Count),
                apartmentsByDistrict.Count,
                existingPhysical,
                assigned,
                abstractResidents,
                unresolved,
                failed,
                failed == 0 ? null : "one-or-more-residence-mutations-failed");
        }
        catch (Exception exception)
        {
            Log.Error($"Living-city residence allocation failed: {exception}");
            return LastReport = Report(NCCityResidenceAllocationStatus.Rejected, error: exception.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<List<NCCityActorRecord>?> ReadActors(NCCityResidenceAllocationPolicyPrototype policy)
    {
        var records = new List<NCCityActorRecord>();
        NCActorId? continuation = null;
        do
        {
            var page = await _actors.GetPageResultAsync(continuation, policy.SnapshotPageSize);
            records.AddRange(page.Records);
            if (records.Count > policy.MaximumActorsPerRun)
                return null;
            continuation = page.HasMore ? page.ContinuationActorId : null;
        } while (continuation is { IsValid: true });
        return records;
    }

    private bool TryResolveDistrict(NCZoneId zoneId, out NCZoneId district)
    {
        district = default;
        var current = zoneId;
        var depth = 0;
        while (current.IsValid && depth++ < 16 && _zones.TryGetZone(current, out var zone))
        {
            if (zone.ContextSlot == NCZoneContextSlot.District)
            {
                district = current;
                return true;
            }
            current = zone.Parent;
        }
        return false;
    }

    private static bool ValidPolicy(NCCityResidenceAllocationPolicyPrototype policy) =>
        policy.SnapshotPageSize is > 0 and <= 1000 &&
        policy.MaximumActorsPerRun > 0 &&
        policy.MaximumConcurrentMutations is > 0 and <= 64 &&
        policy.PhysicalResidentsPerApartment is > 0 and <= 32 &&
        float.IsFinite(policy.PhysicalResidentShare) &&
        policy.PhysicalResidentShare is > 0f and < 0.5f;

    private static NCCityResidenceAllocationReport Report(
        NCCityResidenceAllocationStatus status,
        string? error = null) =>
        new(status, 0, 0, 0, 0, 0, 0, 0, 0, error);
}
