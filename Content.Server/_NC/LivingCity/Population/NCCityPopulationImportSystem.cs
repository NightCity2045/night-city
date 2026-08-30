// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;

namespace Content.Server._NC.LivingCity.Population;

/// <summary>
/// Explicit, bounded and restart-safe importer for persistent city residents. It creates no ECS entities.
/// Every slot has deterministic actor and command identities, so an interrupted import can be safely repeated.
/// </summary>
public sealed partial class NCCityPopulationImportSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultPopulationImportPolicy";
    private const int MaximumTextLength = 128;
    private const int MaximumStableLocalIdLength = 128;
    private const int MaximumAliases = 16;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCLivelihoodRegistrySystem _livelihoods = default!;
    [Dependency] private NCFactionMembershipRegistrySystem _affiliations = default!;

    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private NCCityPopulationImportProgress _progress = new(false, null, 0, 0, 0, 0, 0, 0);

    public NCCityPopulationImportProgress Progress
    {
        get
        {
            lock (_runLock)
                return _progress;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public async Task<NCCityPopulationImportReport> ImportAsync(
        ProtoId<NCCityPopulationProfilePrototype> profileId,
        string auditSource = "system:population-import")
    {
        if (!_prototypes.TryIndex(profileId, out NCCityPopulationProfilePrototype? profile) || profile == null)
            return Rejected(profileId.Id, $"Unknown population profile {profileId}.");
        string? policyError = null;
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityPopulationImportPolicyPrototype>(DefaultPolicyId),
                out NCCityPopulationImportPolicyPrototype? policy) ||
            policy == null || !TryValidatePolicy(policy, out policyError))
        {
            return Rejected(profile.ID, policyError ?? $"Missing population import policy {DefaultPolicyId}.");
        }

        var errors = new List<string>();
        if (!ValidateProfile(profile, policy, errors))
            return Rejected(profile.ID, string.Join(" ", errors));
        if (string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > MaximumTextLength)
            return Rejected(profile.ID, "The audit source is invalid.");
        if (!_clock.TryGetCurrentTime(out _, out _))
            return Rejected(profile.ID, "Population imports require an active-world round.");

        CancellationTokenSource cancellation;
        lock (_runLock)
        {
            if (_runCancellation != null)
            {
                return new NCCityPopulationImportReport(
                    NCCityPopulationImportStatus.AlreadyRunning,
                    profile.ID,
                    CountActors(profile),
                    0,
                    0,
                    0,
                    0,
                    []);
            }

            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            _progress = new NCCityPopulationImportProgress(
                true,
                profile.ID,
                CountActors(profile),
                0,
                0,
                0,
                0,
                0);
        }

        try
        {
            return await ExecuteImport(profile, policy, auditSource.Trim(), cancellation.Token);
        }
        finally
        {
            lock (_runLock)
            {
                if (ReferenceEquals(_runCancellation, cancellation))
                {
                    _runCancellation.Dispose();
                    _runCancellation = null;
                    _progress = _progress with { Running = false };
                }
            }
        }
    }

    public bool ValidateProfile(
        NCCityPopulationProfilePrototype profile,
        NCCityPopulationImportPolicyPrototype policy,
        List<string> errors)
    {
        errors.Clear();
        if (profile.ScopeId == Guid.Empty)
            errors.Add("scopeId must be a non-empty GUID.");
        if (profile.Cohorts.Count == 0 && profile.Actors.Count == 0)
            errors.Add("A population profile must contain at least one cohort or authored actor.");

        var total = CountActors(profile);
        if (total < 1 || total > policy.MaximumActorsPerProfile)
            errors.Add($"Actor count {total} is outside the policy limit 1..{policy.MaximumActorsPerProfile}.");

        var authoredStableIds = new HashSet<string>(StringComparer.Ordinal);
        var replacedSlots = new HashSet<string>(StringComparer.Ordinal);
        var cohortIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cohort in profile.Cohorts)
        {
            if (string.IsNullOrWhiteSpace(cohort.Id) || cohort.Id.Length > MaximumTextLength ||
                !cohortIds.Add(cohort.Id))
            {
                errors.Add("Cohort IDs must be non-empty, bounded and unique.");
                continue;
            }

            if (BuildCohortStableLocalId(profile.ID, cohort.Id, 0).Length > MaximumStableLocalIdLength)
                errors.Add($"Cohort {cohort.Id} produces stable actor IDs longer than {MaximumStableLocalIdLength} characters.");

            if (cohort.Count < 1)
                errors.Add($"Cohort {cohort.Id} must contain at least one actor.");
            if (!_prototypes.TryIndex(cohort.IdentityPool, out NCCityIdentityPoolPrototype? pool) || pool == null)
            {
                errors.Add($"Cohort {cohort.Id} references missing identity pool {cohort.IdentityPool}.");
            }
            else
            {
                ValidateIdentityPool(pool, errors);
                var capacity = (long) pool.GivenNames.Count * pool.FamilyNames.Count;
                if (cohort.Count > capacity)
                    errors.Add($"Cohort {cohort.Id} needs {cohort.Count} unique names but pool capacity is {capacity}.");
                if (capacity > int.MaxValue)
                    errors.Add($"Identity pool {pool.ID} exceeds the supported deterministic permutation size.");
            }

            ValidatePlacement(cohort.Archetype, cohort.HomeZone, cohort.WorkZone, cohort.Job, cohort.Livelihood,
                cohort.Faction, cohort.FactionRole, null,
                $"Cohort {cohort.Id}", errors);
        }

        foreach (var actor in profile.Actors)
        {
            if (string.IsNullOrWhiteSpace(actor.StableLocalId) || actor.StableLocalId.Length > MaximumTextLength)
            {
                errors.Add("Authored actor stableLocalId values must be non-empty and bounded.");
            }
            else
            {
                var replacementRequested = !string.IsNullOrWhiteSpace(actor.ReplacesCohortId) ||
                                           actor.ReplacesCohortIndex >= 0;
                var replacementCohort = replacementRequested
                    ? profile.Cohorts.FirstOrDefault(value => value.Id == actor.ReplacesCohortId)
                    : null;
                var validReplacement = replacementCohort != null && actor.ReplacesCohortIndex >= 0 &&
                                       actor.ReplacesCohortIndex < replacementCohort.Count;
                if (replacementRequested && !validReplacement)
                {
                    errors.Add($"Authored actor {actor.StableLocalId} replaces an invalid cohort slot.");
                }

                var stableId = validReplacement
                    ? BuildCohortStableLocalId(profile.ID, replacementCohort!.Id, actor.ReplacesCohortIndex)
                    : BuildAuthoredStableLocalId(profile.ID, actor.StableLocalId);
                if (stableId.Length > MaximumStableLocalIdLength || !authoredStableIds.Add(stableId))
                    errors.Add("Authored actor stableLocalId values must be unique and fit the persistent identity bound.");
                if (validReplacement && !replacedSlots.Add(stableId))
                    errors.Add($"More than one authored actor replaces cohort slot {stableId}.");
            }

            if (string.IsNullOrWhiteSpace(actor.FullName) || actor.FullName.Length > MaximumTextLength ||
                actor.Aliases.Count > MaximumAliases ||
                actor.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias) || alias.Length > MaximumTextLength))
            {
                errors.Add($"Authored actor {actor.StableLocalId} has invalid identity text.");
            }

            ValidatePlacement(actor.Archetype, actor.HomeZone, actor.WorkZone, actor.Job, actor.Livelihood,
                actor.Faction, actor.FactionRole, actor.NarrativeActorProfile,
                $"Authored actor {actor.StableLocalId}", errors);
        }

        return errors.Count == 0;
    }

    public bool ValidateProfile(ProtoId<NCCityPopulationProfilePrototype> profileId, List<string> errors)
    {
        errors.Clear();
        if (!_prototypes.TryIndex(profileId, out NCCityPopulationProfilePrototype? profile) || profile == null)
        {
            errors.Add($"Unknown population profile {profileId}.");
            return false;
        }

        string? policyError = null;
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityPopulationImportPolicyPrototype>(DefaultPolicyId),
                out NCCityPopulationImportPolicyPrototype? policy) ||
            policy == null || !TryValidatePolicy(policy, out policyError))
        {
            errors.Add(policyError ?? $"Missing population import policy {DefaultPolicyId}.");
            return false;
        }

        return ValidateProfile(profile, policy, errors);
    }

    public static string BuildCohortStableLocalId(string profileId, string cohortId, int zeroBasedIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedIndex);
        return $"population/{profileId}/cohort/{cohortId}/{zeroBasedIndex:D8}";
    }

    public static string BuildAuthoredStableLocalId(string profileId, string stableLocalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLocalId);
        return $"population/{profileId}/actor/{stableLocalId.Trim()}";
    }

    private async Task<NCCityPopulationImportReport> ExecuteImport(
        NCCityPopulationProfilePrototype profile,
        NCCityPopulationImportPolicyPrototype policy,
        string auditSource,
        CancellationToken cancellation)
    {
        var failures = new List<NCCityPopulationImportFailure>();
        var failureLock = new object();
        using var concurrency = new SemaphoreSlim(policy.MaximumConcurrentCreates);
        var pending = new List<Task>(policy.MaximumConcurrentCreates);

        foreach (var candidate in EnumerateCandidates(profile))
        {
            if (cancellation.IsCancellationRequested)
                break;

            try
            {
                await concurrency.WaitAsync(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                break;
            }

            var task = ImportOneBounded(
                candidate,
                profile.ScopeId,
                policy,
                auditSource,
                failures,
                failureLock,
                concurrency,
                cancellation);
            pending.Add(task);

            // Retain only the bounded active window instead of one Task per city resident.
            if (pending.Count >= policy.MaximumConcurrentCreates * 2)
            {
                await Task.WhenAny(pending);
                pending.RemoveAll(static task => task.IsCompleted);
            }
        }

        await Task.WhenAll(pending);
        var progress = Progress;
        return new NCCityPopulationImportReport(
            cancellation.IsCancellationRequested
                ? NCCityPopulationImportStatus.Cancelled
                : NCCityPopulationImportStatus.Completed,
            profile.ID,
            progress.Requested,
            progress.Created,
            progress.Existing,
            progress.Conflicts,
            progress.Failed,
            failures.OrderBy(static failure => failure.StableLocalId, StringComparer.Ordinal).ToArray());
    }

    private async Task ImportOneBounded(
        PopulationCandidate candidate,
        Guid scopeId,
        NCCityPopulationImportPolicyPrototype policy,
        string auditSource,
        List<NCCityPopulationImportFailure> failures,
        object failureLock,
        SemaphoreSlim concurrency,
        CancellationToken cancellation)
    {
        try
        {
            await ImportOne(candidate, scopeId, policy, auditSource, failures, failureLock, cancellation);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ImportOne(
        PopulationCandidate candidate,
        Guid scopeId,
        NCCityPopulationImportPolicyPrototype policy,
        string auditSource,
        List<NCCityPopulationImportFailure> failures,
        object failureLock,
        CancellationToken cancellation)
    {
        NCCityActorMutationResult? result = null;
        for (var attempt = 0; attempt <= policy.StorageRetryCount; attempt++)
        {
            if (cancellation.IsCancellationRequested)
                return;

            result = await _actors.CreateAsync(new NCCityActorCreateRequest(
                CreateDeterministicCommandId(scopeId, candidate.StableLocalId),
                scopeId,
                candidate.StableLocalId,
                candidate.FullName,
                candidate.Aliases,
                candidate.Archetype,
                candidate.HomeZone,
                candidate.WorkZone,
                CreateDeterministicCorrelationId(scopeId, candidate.StableLocalId),
                AuditSource: auditSource,
                NarrativeActorProfile: candidate.NarrativeActorProfile));
            if (result.Status != NCCityActorMutationStatus.StorageFailure || attempt == policy.StorageRetryCount)
                break;

            try
            {
                await Task.Delay(policy.StorageRetryDelay, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
        }

        if (result == null)
            return;

        NCCityActorMutationResult? identityUpdate = null;
        if (candidate.Authored &&
            result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
        {
            identityUpdate = await EnsureAuthoredIdentity(candidate, result.ActorId, scopeId, auditSource,
                cancellation);
        }

        NCWorkforceAssignmentResult? assignment = null;
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate &&
            candidate.Job is { } job)
        {
            assignment = await _workforce.EnsureEmploymentAsync(result.ActorId, job, auditSource);
        }

        NCLivelihoodMutationResult? livelihood = null;
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate &&
            candidate.Livelihood is { } livelihoodProfile)
        {
            var stableKey = $"{result.ActorId}/{livelihoodProfile.Id}";
            livelihood = await _livelihoods.EnsureAsync(new NCEnsureLivelihoodRequest(
                NCPersistentIdentity.Command("population", "livelihood", stableKey),
                result.ActorId,
                livelihoodProfile,
                NCPersistentIdentity.Correlation("population", "livelihood", stableKey),
                AuditSource: auditSource));
        }

        NCFactionMembershipMutationResult? affiliation = null;
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate &&
            candidate.Faction is { } faction)
        {
            var stableKey = $"{result.ActorId}/{faction.Id}";
            affiliation = await _affiliations.EnsureAsync(new NCEnsureFactionMembershipRequest(
                NCPersistentIdentity.Command("population", "affiliation", stableKey),
                result.ActorId,
                faction,
                candidate.FactionRole,
                NCPersistentIdentity.Correlation("population", "affiliation", stableKey),
                AuditSource: auditSource));
        }

        lock (_runLock)
        {
            _progress = result.Status switch
            {
                NCCityActorMutationStatus.Applied => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Created = _progress.Created + 1,
                },
                NCCityActorMutationStatus.Duplicate => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Existing = _progress.Existing + 1,
                },
                NCCityActorMutationStatus.VersionConflict => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Conflicts = _progress.Conflicts + 1,
                },
                _ => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Failed = _progress.Failed + 1,
                },
            };
        }

        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
        {
            var assignmentOk = assignment == null || assignment.Status is NCWorkforceAssignmentStatus.Applied or
                NCWorkforceAssignmentStatus.Existing;
            var livelihoodOk = livelihood == null || livelihood.Status is NCLivelihoodMutationStatus.Applied or
                NCLivelihoodMutationStatus.Duplicate;
            var affiliationOk = affiliation == null || affiliation.Status is NCFactionMembershipMutationStatus.Applied
                or NCFactionMembershipMutationStatus.Duplicate;
            var identityOk = identityUpdate == null || identityUpdate.Status is NCCityActorMutationStatus.Applied or
                NCCityActorMutationStatus.Duplicate;
            if (assignmentOk && livelihoodOk && affiliationOk && identityOk)
            {
                return;
            }

            lock (_runLock)
                _progress = _progress with { Failed = _progress.Failed + 1 };
            lock (failureLock)
            {
                failures.Add(new NCCityPopulationImportFailure(
                    candidate.StableLocalId,
                    result.ActorId,
                    string.Join(" ", new[]
                    {
                        assignmentOk ? null : assignment?.Error ?? assignment?.Status.ToString(),
                        livelihoodOk ? null : livelihood?.Error ?? livelihood?.Status.ToString(),
                        affiliationOk ? null : affiliation?.Error ?? affiliation?.Status.ToString(),
                        identityOk ? null : identityUpdate?.Error ?? identityUpdate?.Status.ToString(),
                    }.Where(static value => !string.IsNullOrWhiteSpace(value)))));
            }
            return;
        }

        lock (failureLock)
        {
            failures.Add(new NCCityPopulationImportFailure(
                candidate.StableLocalId,
                result.ActorId,
                result.Error ?? result.Status.ToString()));
        }
    }

    private IEnumerable<PopulationCandidate> EnumerateCandidates(NCCityPopulationProfilePrototype profile)
    {
        var replacements = profile.Actors
            .Where(static actor => !string.IsNullOrWhiteSpace(actor.ReplacesCohortId) &&
                                   actor.ReplacesCohortIndex >= 0)
            .ToDictionary(actor => (actor.ReplacesCohortId, actor.ReplacesCohortIndex));
        foreach (var cohort in profile.Cohorts.OrderBy(static cohort => cohort.Id, StringComparer.Ordinal))
        {
            var pool = _prototypes.Index(cohort.IdentityPool);
            var capacity = checked(pool.GivenNames.Count * pool.FamilyNames.Count);
            var permutation = CreateNamePermutation(profile.ScopeId, profile.ID, cohort.Id, capacity);
            for (var index = 0; index < cohort.Count; index++)
            {
                if (replacements.TryGetValue((cohort.Id, index), out var authored))
                {
                    yield return BuildAuthoredCandidate(authored,
                        BuildCohortStableLocalId(profile.ID, cohort.Id, index));
                    continue;
                }

                var flatIndex = (permutation.Start + (long) index * permutation.Step) % capacity;
                var givenIndex = (int) (flatIndex % pool.GivenNames.Count);
                var familyIndex = (int) (flatIndex / pool.GivenNames.Count);
                yield return new PopulationCandidate(
                    BuildCohortStableLocalId(profile.ID, cohort.Id, index),
                    $"{pool.GivenNames[givenIndex].Trim()} {pool.FamilyNames[familyIndex].Trim()}",
                    [],
                    cohort.Archetype,
                    cohort.HomeZone,
                    cohort.WorkZone,
                    cohort.Job,
                    cohort.Livelihood,
                    cohort.Faction,
                    cohort.FactionRole,
                    null,
                    false);
            }
        }

        foreach (var actor in profile.Actors
                     .Where(static actor => string.IsNullOrWhiteSpace(actor.ReplacesCohortId))
                     .OrderBy(static actor => actor.StableLocalId, StringComparer.Ordinal))
        {
            yield return BuildAuthoredCandidate(actor,
                BuildAuthoredStableLocalId(profile.ID, actor.StableLocalId));
        }
    }

    private static PopulationCandidate BuildAuthoredCandidate(
        NCCityPopulationActorEntry actor,
        string stableLocalId)
    {
        return new PopulationCandidate(
            stableLocalId,
            actor.FullName.Trim(),
            actor.Aliases.Select(static alias => alias.Trim()).ToArray(),
            actor.Archetype,
            actor.HomeZone,
            actor.WorkZone,
            actor.Job,
            actor.Livelihood,
            actor.Faction,
            actor.FactionRole,
            actor.NarrativeActorProfile,
            true);
    }

    private void ValidatePlacement(
        ProtoId<NCCityActorArchetypePrototype> archetypeId,
        Content.Shared._NC.City.Zones.NCZoneId homeZone,
        Content.Shared._NC.City.Zones.NCZoneId workZone,
        ProtoId<JobPrototype>? jobId,
        ProtoId<NCLivelihoodProfilePrototype>? livelihoodId,
        ProtoId<NCFactionProfilePrototype>? factionId,
        string factionRole,
        ProtoId<NCNarrativeActorProfilePrototype>? narrativeActorProfile,
        string label,
        List<string> errors)
    {
        if (!_prototypes.TryIndex(archetypeId, out NCCityActorArchetypePrototype? archetype) || archetype == null)
        {
            errors.Add($"{label} references missing actor archetype {archetypeId}.");
            return;
        }

        if (!HasSingleTravelMode(archetype.TravelMode) || !Enum.IsDefined(archetype.CognitionTier) ||
            !_prototypes.HasIndex(archetype.EntityPrototype) ||
            !_prototypes.HasIndex(archetype.SpawnCandidateProfile) ||
            !_prototypes.HasIndex(archetype.RoutineProfile))
        {
            errors.Add($"{label} references an incomplete actor archetype {archetypeId}.");
        }

        var narrativeId = narrativeActorProfile ?? archetype.NarrativeActorProfile;
        if (narrativeId is { } selectedNarrativeId &&
            (!_prototypes.TryIndex(selectedNarrativeId, out NCNarrativeActorProfilePrototype? narrative) ||
             narrative == null || !_prototypes.HasIndex(narrative.PersonaProfile) ||
             !_prototypes.HasIndex(narrative.AutonomyPolicy)))
        {
            errors.Add($"{label} references an incomplete narrative actor profile {selectedNarrativeId}.");
        }

        if (!_zones.TryGetZone(homeZone, out var home) || !archetype.AllowedHomeContexts.Contains(home.ContextSlot))
            errors.Add($"{label} has a missing or incompatible home zone.");
        if (workZone.IsValid &&
            (!_zones.TryGetZone(workZone, out var work) || !archetype.AllowedWorkContexts.Contains(work.ContextSlot)))
        {
            errors.Add($"{label} has a missing or incompatible work zone.");
        }

        if (jobId is { } job && !_workforce.TryResolveNpcJob(job, out _, out _, out var jobError))
            errors.Add($"{label} has invalid formal employment: {jobError}");
        if (jobId != null && livelihoodId != null)
            errors.Add($"{label} cannot combine formal employment with a civilian livelihood.");
        if (livelihoodId is { } livelihood && !_prototypes.HasIndex(livelihood))
            errors.Add($"{label} references missing livelihood {livelihood}.");
        if (factionId is { } faction &&
            (!_prototypes.HasIndex(faction) || string.IsNullOrWhiteSpace(factionRole) || factionRole.Length > 64))
        {
            errors.Add($"{label} has invalid faction membership.");
        }
    }

    private static void ValidateIdentityPool(NCCityIdentityPoolPrototype pool, List<string> errors)
    {
        if (pool.GivenNames.Count == 0 || pool.FamilyNames.Count == 0 ||
            pool.GivenNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > MaximumTextLength) ||
            pool.FamilyNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > MaximumTextLength) ||
            pool.GivenNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.GivenNames.Count ||
            pool.FamilyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pool.FamilyNames.Count)
        {
            errors.Add($"Identity pool {pool.ID} must contain unique, non-empty, bounded given and family names.");
        }
    }

    private static bool TryValidatePolicy(NCCityPopulationImportPolicyPrototype policy, out string? error)
    {
        if (policy.MaximumActorsPerProfile is < 1 or > 1000000 ||
            policy.MaximumConcurrentCreates is < 1 or > 64 ||
            policy.StorageRetryCount is < 0 or > 10 ||
            policy.StorageRetryDelay < TimeSpan.Zero || policy.StorageRetryDelay > TimeSpan.FromMinutes(1))
        {
            error = "The population import policy is outside safe operational bounds.";
            return false;
        }

        error = null;
        return true;
    }

    private static int CountActors(NCCityPopulationProfilePrototype profile)
    {
        long total = profile.Actors.Count(static actor => string.IsNullOrWhiteSpace(actor.ReplacesCohortId));
        foreach (var cohort in profile.Cohorts)
            total += Math.Max(0, cohort.Count);
        return total > int.MaxValue ? int.MaxValue : (int) total;
    }

    private static bool IdentityMatches(NCCityActorIdentity identity, PopulationCandidate candidate)
    {
        return identity.FullName == candidate.FullName &&
               identity.Aliases.SequenceEqual(candidate.Aliases, StringComparer.Ordinal) &&
               identity.NarrativeActorProfileId == candidate.NarrativeActorProfile?.Id;
    }

    private async Task<NCCityActorMutationResult?> EnsureAuthoredIdentity(
        PopulationCandidate candidate,
        NCActorId actorId,
        Guid scopeId,
        string auditSource,
        CancellationToken cancellation)
    {
        NCCityActorMutationResult? last = null;
        for (var attempt = 0; attempt < 4 && !cancellation.IsCancellationRequested; attempt++)
        {
            // The original idempotent creation receipt can contain an old snapshot, so identity migration always
            // reads the latest aggregate version before applying an optimistic write.
            var current = await _actors.GetAsync(actorId, true);
            if (current == null)
            {
                return new NCCityActorMutationResult(NCCityActorMutationStatus.NotFound, actorId, 0, null,
                    "The authored population actor disappeared during identity migration.");
            }
            if (IdentityMatches(current.Snapshot.Identity, candidate))
                return null;

            last = await _actors.SetAuthoredIdentityAsync(new NCCityActorIdentityMutationRequest(
                CreateDeterministicIdentityCommandId(scopeId, candidate.StableLocalId, candidate.FullName,
                    candidate.NarrativeActorProfile?.Id, current.AggregateVersion),
                actorId,
                current.AggregateVersion,
                candidate.FullName,
                candidate.Aliases,
                candidate.NarrativeActorProfile,
                CreateDeterministicCorrelationId(scopeId, candidate.StableLocalId),
                AuditSource: auditSource));
            if (last.Status != NCCityActorMutationStatus.VersionConflict)
                return last;
        }

        return last;
    }

    private static (int Start, int Step) CreateNamePermutation(
        Guid scopeId,
        string profileId,
        string cohortId,
        int capacity)
    {
        var hash = Hash(scopeId, $"names/{profileId}/{cohortId}");
        var start = (int) (BitConverter.ToUInt64(hash, 0) % (ulong) capacity);
        var step = (int) (BitConverter.ToUInt64(hash, 8) % (ulong) capacity);
        if (step == 0)
            step = 1;
        while (GreatestCommonDivisor(step, capacity) != 1)
            step = step == capacity - 1 ? 1 : step + 1;
        return (start, step);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static Guid CreateDeterministicCommandId(Guid scopeId, string stableLocalId)
    {
        return GuidFromHash(Hash(scopeId, $"command/create/{stableLocalId}"));
    }

    private static Guid CreateDeterministicIdentityCommandId(
        Guid scopeId,
        string stableLocalId,
        string fullName,
        string? narrativeProfileId,
        long expectedVersion)
    {
        return GuidFromHash(Hash(scopeId,
            $"command/author-identity/{stableLocalId}/{fullName.Trim()}/{narrativeProfileId ?? "resident"}/{expectedVersion}"));
    }

    private static Guid CreateDeterministicCorrelationId(Guid scopeId, string stableLocalId)
    {
        return GuidFromHash(Hash(scopeId, $"correlation/import/{stableLocalId}"));
    }

    private static byte[] Hash(Guid scopeId, string value)
    {
        var scope = scopeId.ToByteArray();
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[scope.Length + text.Length];
        scope.CopyTo(input, 0);
        text.CopyTo(input, scope.Length);
        return SHA256.HashData(input);
    }

    private static Guid GuidFromHash(byte[] hash)
    {
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        lock (_runLock)
            _runCancellation?.Cancel();
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        lock (_runLock)
            _runCancellation?.Cancel();
    }

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private static NCCityPopulationImportReport Rejected(string profileId, string error)
    {
        return new NCCityPopulationImportReport(
            NCCityPopulationImportStatus.Rejected,
            profileId,
            0,
            0,
            0,
            0,
            1,
            [new NCCityPopulationImportFailure(string.Empty, default, error)]);
    }

    private sealed record PopulationCandidate(
        string StableLocalId,
        string FullName,
        string[] Aliases,
        ProtoId<NCCityActorArchetypePrototype> Archetype,
        Content.Shared._NC.City.Zones.NCZoneId HomeZone,
        Content.Shared._NC.City.Zones.NCZoneId WorkZone,
        ProtoId<JobPrototype>? Job,
        ProtoId<NCLivelihoodProfilePrototype>? Livelihood,
        ProtoId<NCFactionProfilePrototype>? Faction,
        string FactionRole,
        ProtoId<NCNarrativeActorProfilePrototype>? NarrativeActorProfile,
        bool Authored);
}
