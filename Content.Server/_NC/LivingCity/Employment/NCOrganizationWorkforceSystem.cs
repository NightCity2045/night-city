// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Clothing.Systems;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment.Components;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Resolves persistent NPC employment against the same DepartmentPrototype and JobPrototype used by players.
/// Player slot limits are never read or modified here; imported actors form an independent A-Life workforce pool.
/// </summary>
public sealed partial class NCOrganizationWorkforceSystem : EntitySystem
{
    private static readonly ProtoId<NCCityWorkforcePolicyPrototype> DefaultPolicy = "NCDefaultWorkforcePolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private OutfitSystem _outfits = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<ProtoId<JobPrototype>, ProtoId<DepartmentPrototype>> _organizationByJob = [];
    private readonly Dictionary<NCOrganizationId, ProtoId<DepartmentPrototype>> _organizationByIdentity = [];
    private readonly Dictionary<NCActorId, (ProtoId<DepartmentPrototype> Organization, ProtoId<JobPrototype> Job)>
        _assignments = [];
    private readonly object _assignmentLock = new();
    private readonly HashSet<NCActorId> _recoveryTouchedActors = [];
    private int _recoveryGeneration;
    private bool _active;

    public string? ConfigurationError { get; private set; }
    public bool RecoveryRunning { get; private set; }
    public bool RecoveryComplete { get; private set; }
    public int RecoveredAssignmentCount { get; private set; }
    public int RecoveryFailureCount { get; private set; }
    public string? LastRecoveryError { get; private set; }

    public int IndexedAssignmentCount
    {
        get
        {
            lock (_assignmentLock)
                return _assignments.Count;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        BuildOrganizationIndex();
        SubscribeLocalEvent<NCPhysicalActorSpawnedEvent>(OnActorRehydrated);
        SubscribeLocalEvent<NCEmploymentPersistedEvent>(OnEmploymentPersisted);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => StopRecovery());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => StopRecovery());
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public bool TryResolveNpcJob(
        ProtoId<JobPrototype> jobId,
        out ProtoId<DepartmentPrototype> organizationId,
        out JobPrototype job,
        out string? error)
    {
        organizationId = default;
        job = default!;
        if (ConfigurationError != null)
        {
            error = ConfigurationError;
            return false;
        }

        if (!_organizationByJob.TryGetValue(jobId, out organizationId) ||
            !_prototypes.TryIndex(jobId, out job!))
        {
            error = $"Job {jobId} is not part of a selectable Night City organization.";
            return false;
        }

        if (!job.NCNpcAllowed)
        {
            error = $"Job {jobId} is reserved for players.";
            return false;
        }

        error = null;
        return true;
    }

    public async Task<NCWorkforceAssignmentResult> EnsureEmploymentAsync(
        NCActorId actorId,
        ProtoId<JobPrototype> jobId,
        string auditSource = "population-workforce")
    {
        if (!actorId.IsValid)
            return Reject(actorId, null, jobId, "The actor identity is invalid.");
        if (!TryResolveNpcJob(jobId, out var organizationId, out var job, out var error))
            return Reject(actorId, organizationId, jobId, error ?? "The job is not available to A-Life actors.");
        if (
            !_prototypes.TryIndex(organizationId, out DepartmentPrototype? organization) ||
            organization == null ||
            !_prototypes.TryIndex(DefaultPolicy, out NCCityWorkforcePolicyPrototype? policy) ||
            policy == null || policy.PayrollInterval <= TimeSpan.Zero || policy.MaximumEmployees < 1)
        {
            return Reject(actorId, organizationId, jobId, "The workforce policy is invalid.");
        }

        var employee = NCPartyRef.ForActor(actorId);
        var employer = NCPersistentIdentity.Organization(organization.ID);
        var employmentId = NCPersistentIdentity.Employment(employee, employer, job.ID);
        var current = await _employment.FindCurrentByEmployeeAsync(employee, true);
        if (current != null)
        {
            if (current.Snapshot.Employer == employer &&
                current.Snapshot.PositionId == job.ID &&
                current.Snapshot.Status == NCEmploymentStatus.Active)
            {
                Remember(actorId, organization.ID, job.ID);
                return new(NCWorkforceAssignmentStatus.Existing, actorId, organization.ID, job.ID,
                    current.Snapshot.EmploymentId, null);
            }

            if (current.Snapshot.Employer == employer &&
                current.Snapshot.PositionId == job.ID &&
                current.Snapshot.Status == NCEmploymentStatus.Suspended)
            {
                RemoveAssignment(actorId);
                return new(NCWorkforceAssignmentStatus.Conflict, actorId, organization.ID, job.ID,
                    current.Snapshot.EmploymentId, "The employment relation is suspended.");
            }

            return new(NCWorkforceAssignmentStatus.Conflict, actorId, organization.ID, job.ID,
                current.Snapshot.EmploymentId, "The actor already has another current employment relation.");
        }

        // Existing relations remain recoverable at the limit; only creation of a new worker consumes capacity.
        if (IndexedAssignmentCount >= policy.MaximumEmployees)
            return Reject(actorId, organizationId, jobId, "The configured A-Life workforce limit was reached.");

        var registered = await _finance.EnsureOrganizationAsync(
            organization.ID,
            Loc.GetString(organization.Name),
            auditSource);
        if (!registered.IsResolved)
            return Failed(actorId, organization.ID, job.ID, employmentId, registered.Error);

        var employerAccount = await _finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            employer,
            organization.StartingBudget,
            auditSource);
        var payeeAccount = await _finance.EnsurePrimaryAccountAsync(employee, auditSource);
        if (!employerAccount.IsResolved || !payeeAccount.IsResolved)
        {
            return Failed(actorId, organization.ID, job.ID, employmentId,
                employerAccount.Error ?? payeeAccount.Error ?? "A payroll account is unavailable.");
        }

        var index = await _employment.GetEmployeeIndexAsync(employee, true);
        var salary = Math.Max(0, job.Salary);
        var result = await _employment.HireAsync(new NCHirePartyRequest(
            NCPersistentIdentity.Command("workforce", "hire", employmentId.ToString()),
            employmentId,
            employee,
            employer,
            job.ID,
            index?.AggregateVersion ?? 0,
            salary > 0 ? employerAccount.AccountId : default,
            salary > 0 ? payeeAccount.AccountId : default,
            salary,
            salary > 0 ? policy.PayrollInterval.Ticks : 0,
            employer,
            NCPersistentIdentity.Correlation("workforce", "hire", employmentId.ToString()),
            AuditSource: auditSource));

        if (result.Status is NCEmploymentMutationStatus.Applied or NCEmploymentMutationStatus.Duplicate)
        {
            Remember(actorId, organization.ID, job.ID);
            return new(result.Status == NCEmploymentMutationStatus.Applied
                    ? NCWorkforceAssignmentStatus.Applied
                    : NCWorkforceAssignmentStatus.Existing,
                actorId, organization.ID, job.ID, employmentId, null);
        }

        return result.Status == NCEmploymentMutationStatus.VersionConflict
            ? new(NCWorkforceAssignmentStatus.Conflict, actorId, organization.ID, job.ID, employmentId, result.Error)
            : Failed(actorId, organization.ID, job.ID, employmentId, result.Error);
    }

    public void GetPositionProjections(List<NCWorkforcePositionProjection> result)
    {
        result.Clear();
        lock (_assignmentLock)
        {
            foreach (var group in _assignments.Values
                         .GroupBy(static value => value)
                         .OrderBy(static value => value.Key.Organization.Id, StringComparer.Ordinal)
                         .ThenBy(static value => value.Key.Job.Id, StringComparer.Ordinal))
            {
                result.Add(new NCWorkforcePositionProjection(
                    group.Key.Organization,
                    group.Key.Job,
                    group.Count()));
            }
        }
    }

    public void GetActorProjections(List<NCWorkforceActorProjection> result)
    {
        result.Clear();
        lock (_assignmentLock)
        {
            foreach (var (actorId, assignment) in _assignments
                         .OrderBy(static value => value.Key.Value))
            {
                result.Add(new NCWorkforceActorProjection(actorId, assignment.Organization, assignment.Job));
            }
        }
    }

    public bool TryGetAssignment(
        NCActorId actorId,
        out ProtoId<DepartmentPrototype> organization,
        out ProtoId<JobPrototype> job)
    {
        lock (_assignmentLock)
        {
            if (_assignments.TryGetValue(actorId, out var assignment))
            {
                organization = assignment.Organization;
                job = assignment.Job;
                return true;
            }
        }

        organization = default;
        job = default;
        return false;
    }

    private void BuildOrganizationIndex()
    {
        _organizationByJob.Clear();
        _organizationByIdentity.Clear();
        ConfigurationError = null;
        foreach (var organization in _prototypes.EnumeratePrototypes<DepartmentPrototype>()
                     .Where(static value => value.NCSelectable)
                     .OrderBy(static value => value.ID, StringComparer.Ordinal))
        {
            var party = NCPersistentIdentity.Organization(organization.ID);
            var identity = new NCOrganizationId(party.PersistentId);
            _organizationByIdentity[identity] = organization.ID;
            foreach (var job in organization.Roles)
            {
                if (_organizationByJob.TryGetValue(job, out var other) && other != organization.ID)
                {
                    ConfigurationError = $"Job {job} belongs to both {other} and {organization.ID}.";
                    Log.Error(ConfigurationError);
                    return;
                }

                _organizationByJob[job] = organization.ID;
            }
        }
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<DepartmentPrototype>() || args.WasModified<JobPrototype>())
        {
            BuildOrganizationIndex();
            if (_active)
                StartRecovery();
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        StartRecovery();
    }

    private void OnEmploymentPersisted(NCEmploymentPersistedEvent args)
    {
        if (!_active || !args.Employee.TryGetActor(out var actorId))
            return;

        _recoveryTouchedActors.Add(actorId);
        if (args.Status != NCEmploymentStatus.Active)
        {
            RemoveAssignment(actorId);
            return;
        }

        var generation = _recoveryGeneration;
        _ = RefreshEmploymentAsync(generation, args.EmploymentId, actorId);
    }

    private void StartRecovery()
    {
        _recoveryGeneration++;
        _recoveryTouchedActors.Clear();
        RecoveryComplete = false;
        RecoveryRunning = false;
        RecoveredAssignmentCount = 0;
        LastRecoveryError = null;

        if (!_prototypes.TryIndex(DefaultPolicy, out NCCityWorkforcePolicyPrototype? policy) || policy == null ||
            policy.RecoveryPageSize is < 1 or > 500 ||
            policy.MaximumRecoveryRecords < policy.MaximumEmployees)
        {
            RecoveryFailureCount++;
            LastRecoveryError = "The workforce recovery policy is invalid.";
            return;
        }

        RecoveryRunning = true;
        _ = RecoverAssignmentsAsync(_recoveryGeneration, policy.RecoveryPageSize, policy.MaximumRecoveryRecords);
    }

    private async Task RecoverAssignmentsAsync(int generation, int pageSize, int maximumRecords)
    {
        var recovered = new Dictionary<NCActorId, NCEmploymentRecord>();
        NCEmploymentId? after = null;
        var inspected = 0;
        var reachedEnd = false;
        string? error = null;
        try
        {
            while (inspected < maximumRecords)
            {
                var page = await _employment.GetPageResultAsync(after,
                    Math.Min(pageSize, maximumRecords - inspected));
                inspected += page.SourceRowCount;
                foreach (var record in page.Records)
                {
                    var snapshot = record.Snapshot;
                    if (snapshot.Status != NCEmploymentStatus.Active ||
                        !snapshot.Employee.TryGetActor(out var actorId))
                    {
                        continue;
                    }

                    if (!recovered.TryGetValue(actorId, out var current) ||
                        record.AggregateVersion > current.AggregateVersion)
                    {
                        recovered[actorId] = record;
                    }
                }

                if (!page.HasMore)
                {
                    reachedEnd = true;
                    break;
                }
                if (page.ContinuationEmploymentId is not { IsValid: true } continuation)
                    throw new InvalidOperationException("Employment recovery returned an invalid continuation.");
                after = continuation;
            }

            if (!reachedEnd && inspected >= maximumRecords)
            {
                // A full final page is ambiguous: probe once so exactly-at-limit databases do not report a false
                // failure while genuinely truncated history remains visible to administrators.
                var probe = await _employment.GetPageResultAsync(after, 1);
                if (probe.SourceRowCount > 0)
                {
                    throw new InvalidOperationException(
                        "Employment recovery reached its configured historical-row limit.");
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() => ApplyRecovery(generation, recovered, error));
    }

    private void ApplyRecovery(
        int generation,
        IReadOnlyDictionary<NCActorId, NCEmploymentRecord> recovered,
        string? error)
    {
        if (!_active || generation != _recoveryGeneration)
            return;

        RecoveryRunning = false;
        if (error != null)
        {
            RecoveryFailureCount++;
            LastRecoveryError = error;
            return;
        }

        var next = new Dictionary<NCActorId,
            (ProtoId<DepartmentPrototype> Organization, ProtoId<JobPrototype> Job)>();
        foreach (var (actorId, employment) in recovered)
        {
            // A live mutation that arrived after recovery began is newer than the scanned page and wins.
            if (_recoveryTouchedActors.Contains(actorId) ||
                !TryResolveEmployment(employment, out var organization, out var job))
            {
                continue;
            }

            next[actorId] = (organization, job.ID);
        }

        List<(NCActorId ActorId, ProtoId<DepartmentPrototype> Organization, ProtoId<JobPrototype> Job)> changed = [];
        List<(NCActorId ActorId, ProtoId<DepartmentPrototype> Organization, ProtoId<JobPrototype> Job)> removed = [];
        lock (_assignmentLock)
        {
            foreach (var (actorId, assignment) in next)
            {
                if (!_assignments.TryGetValue(actorId, out var current) || current != assignment)
                {
                    _assignments[actorId] = assignment;
                    changed.Add((actorId, assignment.Organization, assignment.Job));
                }
            }

            foreach (var (actorId, assignment) in _assignments.ToArray())
            {
                if (_recoveryTouchedActors.Contains(actorId) || next.ContainsKey(actorId))
                    continue;
                _assignments.Remove(actorId);
                removed.Add((actorId, assignment.Organization, assignment.Job));
            }
        }

        foreach (var assignment in removed)
            ApplyRemoval(assignment.ActorId, assignment.Organization, assignment.Job);
        foreach (var assignment in changed)
        {
            if (_runtime.TryGetEntity(assignment.ActorId, out var entityUid) &&
                _prototypes.TryIndex(assignment.Job, out JobPrototype? job) && job != null)
            {
                ApplyPhysicalEmployment(assignment.ActorId, entityUid, assignment.Organization, job);
            }
            RaiseLocalEvent(new NCWorkforceAssignmentChangedEvent(
                assignment.ActorId, assignment.Organization, assignment.Job));
        }

        RecoveredAssignmentCount = next.Count;
        RecoveryComplete = true;
    }

    private async Task RefreshEmploymentAsync(int generation, NCEmploymentId employmentId, NCActorId actorId)
    {
        NCEmploymentRecord? employment = null;
        string? error = null;
        try
        {
            employment = await _employment.GetAsync(employmentId, true);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() =>
        {
            if (!_active || generation != _recoveryGeneration)
                return;
            if (error != null)
            {
                RecoveryFailureCount++;
                LastRecoveryError = error;
                return;
            }
            if (employment == null || employment.Snapshot.Status != NCEmploymentStatus.Active ||
                !TryResolveEmployment(employment, out var organization, out var job))
            {
                RemoveAssignment(actorId);
                return;
            }
            Remember(actorId, organization, job.ID);
        });
    }

    private bool TryResolveEmployment(
        NCEmploymentRecord employment,
        out ProtoId<DepartmentPrototype> organizationId,
        out JobPrototype job)
    {
        organizationId = default;
        job = default!;
        if (employment.Snapshot.Status != NCEmploymentStatus.Active ||
            !employment.Snapshot.Employer.TryGetOrganization(out var organizationIdentity) ||
            !_organizationByIdentity.TryGetValue(organizationIdentity, out organizationId) ||
            !_prototypes.TryIndex<JobPrototype>(employment.Snapshot.PositionId, out var resolvedJob) ||
            resolvedJob == null ||
            !_organizationByJob.TryGetValue(resolvedJob.ID, out var jobOrganization) ||
            jobOrganization != organizationId || !resolvedJob.NCNpcAllowed)
        {
            return false;
        }

        job = resolvedJob;
        return true;
    }

    private void StopRecovery()
    {
        _active = false;
        _recoveryGeneration++;
        _recoveryTouchedActors.Clear();
        RecoveryRunning = false;
        RecoveryComplete = false;
    }

    private void OnActorRehydrated(NCPhysicalActorSpawnedEvent args)
    {
        _ = ResolvePhysicalEmploymentAsync(args.ActorId, args.EntityUid);
    }

    private async Task ResolvePhysicalEmploymentAsync(NCActorId actorId, EntityUid entityUid)
    {
        var employment = await _employment.FindCurrentByEmployeeAsync(NCPartyRef.ForActor(actorId));
        if (employment?.Snapshot.Status != NCEmploymentStatus.Active ||
            !employment.Snapshot.Employer.TryGetOrganization(out var organizationIdentity) ||
            !_organizationByIdentity.TryGetValue(organizationIdentity, out var organizationId) ||
            !_prototypes.TryIndex<JobPrototype>(employment.Snapshot.PositionId, out var job) ||
            !_organizationByJob.TryGetValue(job.ID, out var jobOrganization) || jobOrganization != organizationId)
        {
            return;
        }

        _taskManager.RunOnMainThread(() => ApplyPhysicalEmployment(actorId, entityUid, organizationId, job));
    }

    private void ApplyPhysicalEmployment(
        NCActorId actorId,
        EntityUid entityUid,
        ProtoId<DepartmentPrototype> organizationId,
        JobPrototype job)
    {
        if (!Exists(entityUid) || !_runtime.TryGetActor(entityUid, out var boundActor) || boundActor != actorId)
            return;

        var worker = EnsureComp<NCCityWorkerComponent>(entityUid);
        worker.Organization = organizationId;
        worker.Job = job.ID;
        Remember(actorId, organizationId, job.ID);

        // Starting gear is applied once. The inventory adapter persists the loadout marker and the resulting physical
        // inventory is captured by the normal dematerialization boundary.
        if (job.StartingGear is not { } startingGear ||
            !TryComp<NCCityActorInventoryAdapterComponent>(entityUid, out var inventory) ||
            !string.IsNullOrWhiteSpace(inventory.EquippedLoadoutId))
        {
            return;
        }

        if (_outfits.SetOutfit(entityUid, startingGear.Id))
            inventory.EquippedLoadoutId = startingGear.Id;
    }

    private void Remember(
        NCActorId actorId,
        ProtoId<DepartmentPrototype> organization,
        ProtoId<JobPrototype> job)
    {
        var changed = false;
        lock (_assignmentLock)
        {
            var next = (Organization: organization, Job: job);
            changed = !_assignments.TryGetValue(actorId, out var current) || current != next;
            _assignments[actorId] = next;
        }

        if (!changed)
            return;

        // Employment I/O completes asynchronously; ECS consumers are notified only on the simulation thread.
        _taskManager.RunOnMainThread(() =>
            RaiseLocalEvent(new NCWorkforceAssignmentChangedEvent(actorId, organization, job)));
    }

    private void RemoveAssignment(NCActorId actorId)
    {
        (ProtoId<DepartmentPrototype> Organization, ProtoId<JobPrototype> Job) removed;
        lock (_assignmentLock)
        {
            if (!_assignments.Remove(actorId, out removed))
                return;
        }

        // Employment mutations may complete off the simulation thread; component removal and events remain main-thread.
        _taskManager.RunOnMainThread(() => ApplyRemoval(actorId, removed.Organization, removed.Job));
    }

    private void ApplyRemoval(
        NCActorId actorId,
        ProtoId<DepartmentPrototype> organization,
        ProtoId<JobPrototype> job)
    {
        if (_runtime.TryGetEntity(actorId, out var entityUid) && Exists(entityUid))
            RemComp<NCCityWorkerComponent>(entityUid);
        RaiseLocalEvent(new NCWorkforceAssignmentChangedEvent(actorId, organization, job, false));
    }

    private static NCWorkforceAssignmentResult Reject(
        NCActorId actorId,
        ProtoId<DepartmentPrototype>? organization,
        ProtoId<JobPrototype>? job,
        string error) =>
        new(NCWorkforceAssignmentStatus.Rejected, actorId, organization, job, default, error);

    private static NCWorkforceAssignmentResult Failed(
        NCActorId actorId,
        ProtoId<DepartmentPrototype> organization,
        ProtoId<JobPrototype> job,
        NCEmploymentId employmentId,
        string? error) =>
        new(NCWorkforceAssignmentStatus.Failed, actorId, organization, job, employmentId,
            error ?? "The workforce assignment failed.");
}
