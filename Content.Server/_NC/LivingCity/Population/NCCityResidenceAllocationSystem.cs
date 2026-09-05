// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Identity;
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
    int ResidenceProfiles,
    int ExistingResidences,
    int AssignedResidences,
    int AbstractHomeActors,
    int UnassignedResidences,
    int FailedMutations,
    string? Error);

/// <summary>
/// Idempotently assigns canonical coordinate-free homes and persists normalization of legacy PhysicalHome records.
/// It never reads apartment geometry and never creates an ECS entity, map marker, room, or property occupancy.
/// </summary>
public sealed partial class NCCityResidenceAllocationSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultResidenceAllocationPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;

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

            var records = await ReadActors(policy);
            if (records == null)
            {
                return LastReport = Report(NCCityResidenceAllocationStatus.Rejected,
                    error: "actor-count-exceeds-residence-policy");
            }

            var existingResidences = 0;
            var abstractHomeActors = 0;
            var missingResidences = new List<NCActorId>();
            var unresolved = 0;
            foreach (var actor in records)
            {
                var homeZone = actor.Snapshot.Location.HomeZone;
                if (!_zones.TryGetZone(homeZone, out _))
                {
                    unresolved++;
                    continue;
                }

                if (actor.Snapshot.Schedule.PresenceMode == NCCityActorPresenceMode.AbstractHome)
                    abstractHomeActors++;
                if (NCCityActorRegistrySystem.HasCanonicalAbstractResidence(actor.Snapshot))
                    existingResidences++;
                else
                    missingResidences.Add(actor.Snapshot.Identity.ActorId);
            }

            var assigned = 0;
            var failed = 0;
            for (var offset = 0; offset < missingResidences.Count; offset += policy.MaximumConcurrentMutations)
            {
                var batch = missingResidences.Skip(offset).Take(policy.MaximumConcurrentMutations)
                    .Select(actorId => EnsureResidenceAsync(actorId, auditSource, policy))
                    .ToArray();
                var results = await Task.WhenAll(batch);
                assigned += results.Count(static result => result == ResidenceEnsureStatus.Assigned);
                existingResidences += results.Count(static result => result == ResidenceEnsureStatus.Existing);
                unresolved += results.Count(static result => result == ResidenceEnsureStatus.Unresolved);
                failed += results.Count(static result => result == ResidenceEnsureStatus.Failed);
            }

            var status = failed == 0 && unresolved == 0
                ? NCCityResidenceAllocationStatus.Completed
                : NCCityResidenceAllocationStatus.Partial;
            return LastReport = new NCCityResidenceAllocationReport(
                status,
                records.Count,
                policy.ResidenceProfiles.Count,
                existingResidences,
                assigned,
                abstractHomeActors,
                unresolved + failed,
                failed,
                failed == 0 && unresolved == 0 ? null : "one-or-more-residences-could-not-be-normalized");
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

    private async Task<ResidenceEnsureStatus> EnsureResidenceAsync(
        NCActorId actorId,
        string auditSource,
        NCCityResidenceAllocationPolicyPrototype policy)
    {
        for (var attempt = 0; attempt <= policy.StorageRetryCount; attempt++)
        {
            var actor = await _actors.GetAsync(actorId, true);
            if (actor == null)
                return ResidenceEnsureStatus.Unresolved;
            if (NCCityActorRegistrySystem.HasCanonicalAbstractResidence(actor.Snapshot))
                return ResidenceEnsureStatus.Existing;
            if (!_zones.TryGetZone(actor.Snapshot.Location.HomeZone, out _))
                return ResidenceEnsureStatus.Unresolved;

            var stableKey = $"{actorId}/{actor.Snapshot.Location.HomeZone}/{actor.AggregateVersion}";
            var result = await _actors.SetResidenceAsync(new NCCityActorResidenceMutationRequest(
                NCPersistentIdentity.Command("residence", "ensure", stableKey),
                actorId,
                actor.AggregateVersion,
                actor.Snapshot.Location.HomeZone,
                NCPersistentIdentity.Correlation("residence", "ensure", actorId.ToString()),
                AuditSource: auditSource));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return ResidenceEnsureStatus.Assigned;
            if (result.Status is not (NCCityActorMutationStatus.VersionConflict or
                NCCityActorMutationStatus.StorageFailure) || attempt == policy.StorageRetryCount)
            {
                return ResidenceEnsureStatus.Failed;
            }

            await Task.Delay(policy.StorageRetryDelay);
        }

        return ResidenceEnsureStatus.Failed;
    }

    private static bool ValidPolicy(NCCityResidenceAllocationPolicyPrototype policy) =>
        policy.SnapshotPageSize is > 0 and <= 1000 &&
        policy.MaximumActorsPerRun > 0 &&
        policy.MaximumConcurrentMutations is > 0 and <= 64 &&
        policy.ResidenceProfiles.Count is > 0 and <= 64 &&
        policy.ResidenceProfiles.Distinct().Count() == policy.ResidenceProfiles.Count &&
        policy.StorageRetryCount is >= 0 and <= 10 &&
        policy.StorageRetryDelay >= TimeSpan.Zero && policy.StorageRetryDelay <= TimeSpan.FromMinutes(1);

    private static NCCityResidenceAllocationReport Report(
        NCCityResidenceAllocationStatus status,
        string? error = null) =>
        new(status, 0, 0, 0, 0, 0, 0, 0, error);

    private enum ResidenceEnsureStatus : byte
    {
        Existing,
        Assigned,
        Unresolved,
        Failed,
    }
}
