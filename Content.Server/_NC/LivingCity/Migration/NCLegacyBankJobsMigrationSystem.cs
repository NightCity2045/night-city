// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Migration;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Migration;

/// <summary>
/// Explicit Bank/Jobs backfill and dual-read rehearsal. It never writes a legacy row and never runs automatically.
/// Keyset pages keep memory bounded; deterministic commands plus provenance bindings make an interrupted apply safe to
/// repeat while refusing to overwrite common-domain changes that do not match the last imported projection.
/// </summary>
public sealed partial class NCLegacyBankJobsMigrationSystem : EntitySystem
{
    private const int PageSize = 128;
    private const int MaximumIssues = 100;
    private const int MaximumAuditSourceLength = 128;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCLegacyMigrationBindingSystem _bindings = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly object _runLock = new();
    private CancellationTokenSource? _runCancellation;
    private NCLegacyBankJobsMigrationProgress _progress = new(
        false,
        NCLegacyMigrationRunMode.Verify,
        NCLegacyMigrationPhase.Idle,
        0,
        0,
        0,
        0,
        0,
        null);

    public NCLegacyBankJobsMigrationProgress Progress
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

    public async Task<NCLegacyBankJobsMigrationReport> RehearseAsync(
        NCLegacyMigrationRunMode mode,
        string auditSource = "system:legacy-bank-jobs-migration")
    {
        if (!Enum.IsDefined(mode) || string.IsNullOrWhiteSpace(auditSource) ||
            auditSource.Length > MaximumAuditSourceLength || !_clock.TryGetCurrentTime(out _, out _))
        {
            return EmptyReport(NCLegacyMigrationRunStatus.Rejected, mode,
                "The migration mode, audit source, or active-world clock is invalid.");
        }

        CancellationTokenSource cancellation;
        lock (_runLock)
        {
            if (_runCancellation != null)
                return EmptyReport(NCLegacyMigrationRunStatus.AlreadyRunning, mode, "A rehearsal is already running.");

            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            _progress = new NCLegacyBankJobsMigrationProgress(
                true,
                mode,
                NCLegacyMigrationPhase.CharacterBank,
                0,
                0,
                0,
                0,
                0,
                null);
        }

        var issues = new List<NCLegacyMigrationIssue>();
        try
        {
            var departments = BuildDepartmentIndex();
            NCPartyAccountResolution? clearing = null;
            if (mode == NCLegacyMigrationRunMode.Apply)
            {
                clearing = await _finance.EnsureLegacyClearingAccountAsync(auditSource.Trim());
                if (!clearing.IsResolved)
                {
                    AddRunFailure(
                        issues,
                        NCLegacySourceKind.CharacterBank,
                        "system:legacy-bank-clearing",
                        clearing.Error ?? "The migration clearing account is unavailable.");
                    return BuildReport(NCLegacyMigrationRunStatus.Rejected, mode, issues);
                }
            }

            await ProcessCharacterBanks(mode, clearing, auditSource.Trim(), issues, cancellation.Token);
            SetPhase(NCLegacyMigrationPhase.OrganizationBank);
            await ProcessOrganizationBanks(mode, clearing, departments, auditSource.Trim(), issues, cancellation.Token);
            SetPhase(NCLegacyMigrationPhase.CharacterEmployment);
            await ProcessEmployments(mode, departments, auditSource.Trim(), issues, cancellation.Token);
            SetPhase(NCLegacyMigrationPhase.Completed);
            return BuildReport(NCLegacyMigrationRunStatus.Completed, mode, issues);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return BuildReport(NCLegacyMigrationRunStatus.Cancelled, mode, issues);
        }
        catch (Exception exception)
        {
            Log.Error($"Legacy Bank/Jobs {mode} rehearsal failed: {exception}");
            AddRunFailure(
                issues,
                SourceKindForPhase(Progress.Phase),
                "run",
                exception.Message);
            return BuildReport(NCLegacyMigrationRunStatus.Failed, mode, issues);
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

    private async Task ProcessCharacterBanks(
        NCLegacyMigrationRunMode mode,
        NCPartyAccountResolution? clearing,
        string auditSource,
        List<NCLegacyMigrationIssue> issues,
        CancellationToken cancellation)
    {
        var after = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = await _database.GetNCLegacyCharacterBankPageAsync(after, PageSize);
            if (page.Count == 0)
                return;

            foreach (var row in page)
            {
                cancellation.ThrowIfCancellationRequested();
                RowOutcome outcome;
                try
                {
                    var sourceKey = $"character:{row.CharacterId.Value}";
                    var revision = NCLegacyBankJobsIdentity.Revision(row.Balance, row.UpdatedAt.Ticks);
                    var party = NCPartyRef.ForCharacter(row.CharacterId);
                    outcome = await ProcessBankProjection(
                        mode,
                        NCLegacySourceKind.CharacterBank,
                        sourceKey,
                        revision,
                        party,
                        row.Balance,
                        clearing,
                        () => _finance.EnsurePrimaryAccountAsync(party, auditSource),
                        auditSource);
                }
                catch (Exception exception)
                {
                    outcome = Failure(NCLegacySourceKind.CharacterBank, $"character:{row.CharacterId.Value}", exception);
                }
                AddOutcome(issues, outcome);
            }

            after = page[^1].CharacterId.Value;
            if (page.Count < PageSize)
                return;
        }
    }

    private async Task ProcessOrganizationBanks(
        NCLegacyMigrationRunMode mode,
        NCPartyAccountResolution? clearing,
        IReadOnlyDictionary<string, DepartmentPrototype?> departments,
        string auditSource,
        List<NCLegacyMigrationIssue> issues,
        CancellationToken cancellation)
    {
        string? after = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = await _database.GetNCLegacyOrganizationBankPageAsync(after, PageSize);
            if (page.Count == 0)
                return;

            foreach (var row in page)
            {
                cancellation.ThrowIfCancellationRequested();
                var sourceKey = $"department:{row.OrganizationPrototypeId}";
                RowOutcome outcome;
                try
                {
                    if (!departments.TryGetValue(row.OrganizationPrototypeId, out var department) || department == null)
                    {
                        outcome = Issue(
                            NCLegacySourceKind.OrganizationBank,
                            sourceKey,
                            NCLegacyMigrationIssueKind.InvalidLegacyData,
                            "The organization prototype is missing or maps its jobs ambiguously.");
                    }
                    else
                    {
                        var revision = NCLegacyBankJobsIdentity.Revision(row.Balance, row.UpdatedAt.Ticks);
                        var party = NCLegacyBankJobsIdentity.OrganizationParty(row.OrganizationPrototypeId);
                        outcome = await ProcessBankProjection(
                            mode,
                            NCLegacySourceKind.OrganizationBank,
                            sourceKey,
                            revision,
                            party,
                            row.Balance,
                            clearing,
                            () => _finance.EnsureLegacyOrganizationAsync(
                                row.OrganizationPrototypeId,
                                Loc.GetString(department.Name),
                                auditSource),
                            auditSource);
                    }
                }
                catch (Exception exception)
                {
                    outcome = Failure(NCLegacySourceKind.OrganizationBank, sourceKey, exception);
                }
                AddOutcome(issues, outcome);
            }

            after = page[^1].OrganizationPrototypeId;
            if (page.Count < PageSize)
                return;
        }
    }

    private async Task<RowOutcome> ProcessBankProjection(
        NCLegacyMigrationRunMode mode,
        NCLegacySourceKind sourceKind,
        string sourceKey,
        string sourceRevision,
        NCPartyRef owner,
        long legacyBalance,
        NCPartyAccountResolution? clearing,
        Func<Task<NCPartyAccountResolution>> ensureAccount,
        string auditSource)
    {
        if (legacyBalance < 0)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.InvalidLegacyData,
                "A legacy account has a negative balance.");

        var bindingId = NCLegacyBankJobsIdentity.Binding(sourceKind.ToString(), sourceKey);
        var accountId = NCLegacyBankJobsIdentity.PrimaryAccount(owner);
        var binding = await _bindings.GetAsync(bindingId, true);
        if (binding != null &&
            (binding.Snapshot.SourceKind != sourceKind || binding.Snapshot.SourceKey != sourceKey ||
             binding.Snapshot.PrimaryParty != owner || binding.Snapshot.AccountId != accountId))
        {
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                "The provenance binding points to another party or account.");
        }

        if (mode == NCLegacyMigrationRunMode.Verify)
            return await VerifyBankProjection(sourceKind, sourceKey, sourceRevision, legacyBalance, binding, accountId);

        var resolution = await ensureAccount();
        if (!resolution.IsResolved)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.StorageFailure,
                resolution.Error ?? "The common account is unavailable.", true);

        var account = await _ledger.GetAccountAsync(accountId, true);
        if (account == null)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.StorageFailure,
                "The ensured common account cannot be reloaded.", true);

        var currentHash = BankProjectionHash(account);
        var transactionOperation = binding == null ? "opening-balance" : "legacy-balance-adjustment";
        var expectedTransactionId = NCLegacyBankJobsIdentity.Transaction(
            transactionOperation,
            sourceKey,
            sourceRevision);
        var completedTransaction = await _ledger.GetTransactionAsync(expectedTransactionId, true);
        var transactionId = binding?.Snapshot.LastTransactionId ??
                            (completedTransaction != null ? expectedTransactionId : default);
        if (binding != null)
        {
            var matchesPreviousProjection = currentHash == binding.Snapshot.ProjectionHash &&
                                            account.Snapshot.Balance == binding.Snapshot.ProjectedBalance;
            // A ledger transaction and its binding are separate durable commits. On restart, accept only the exact
            // deterministic transaction for this newer source revision as proof that the first commit succeeded.
            var resumesCompletedProjection = binding.Snapshot.SourceRevision != sourceRevision &&
                                             completedTransaction != null &&
                                             account.Snapshot.Balance == legacyBalance;
            if (!matchesPreviousProjection && !resumesCompletedProjection)
            {
                return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                    "The common account changed after its last imported projection.");
            }

            if (binding.Snapshot.SourceRevision == sourceRevision)
            {
                return account.Snapshot.Balance == legacyBalance
                    ? new RowOutcome(RowOutcomeKind.Aligned, null)
                    : Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                        "The binding revision matches but the projected balance differs.");
            }
        }
        else if (account.AggregateVersion > 1 && completedTransaction == null)
        {
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                "An unbound deterministic account already contains non-migration activity.");
        }

        if (completedTransaction != null && account.Snapshot.Balance != legacyBalance)
        {
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                "The common account changed after its deterministic migration transaction completed.");
        }

        var changed = false;
        var delta = legacyBalance - account.Snapshot.Balance;
        if (delta != 0)
        {
            if (clearing?.IsResolved != true)
                return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.StorageFailure,
                    "The migration clearing account is unavailable.", true);

            transactionId = expectedTransactionId;

            var clearingAccount = await _ledger.GetAccountAsync(clearing.AccountId, true);
            account = await _ledger.GetAccountAsync(accountId, true);
            if (clearingAccount == null || account == null)
                return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.StorageFailure,
                    "A ledger account disappeared during the migration.", true);

            var posting = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                NCLegacyBankJobsIdentity.Command("post-bank-projection", sourceKey, sourceRevision),
                transactionId,
                binding == null ? NCLedgerTransactionKind.OpeningBalance : NCLedgerTransactionKind.Adjustment,
                $"legacy {sourceKind} projection {sourceKey}",
                [
                    new NCLedgerLegRequest(clearingAccount.Snapshot.AccountId,
                        clearingAccount.AggregateVersion, -delta),
                    new NCLedgerLegRequest(account.Snapshot.AccountId, account.AggregateVersion, delta),
                ],
                clearing.Owner,
                NCLegacyBankJobsIdentity.Command("correlation-bank-projection", sourceKey, sourceRevision),
                AuditSource: auditSource));
            if (posting.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            {
                return Issue(sourceKind, sourceKey,
                    posting.Status == NCEconomyMutationStatus.StorageFailure
                        ? NCLegacyMigrationIssueKind.StorageFailure
                        : NCLegacyMigrationIssueKind.TargetConflict,
                    posting.Error ?? "The balance projection transaction was rejected.",
                    posting.Status == NCEconomyMutationStatus.StorageFailure);
            }
            changed |= posting.Status == NCEconomyMutationStatus.Applied;
        }

        account = await _ledger.GetAccountAsync(accountId, true);
        if (account == null || account.Snapshot.Balance != legacyBalance)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                "The common account does not equal the legacy balance after replay.");

        var projectionHash = BankProjectionHash(account);
        var template = new NCLegacyMigrationBindingSnapshot(
            bindingId,
            sourceKind,
            sourceKey,
            sourceRevision,
            projectionHash,
            legacyBalance,
            owner,
            default,
            accountId,
            default,
            transactionId,
            default,
            binding?.Snapshot.DataVersion ?? 0,
            default);
        var advanced = await _bindings.AdvanceAsync(new NCAdvanceLegacyMigrationBindingRequest(
            NCLegacyBankJobsIdentity.Command("advance-bank-binding", sourceKey, sourceRevision),
            binding?.AggregateVersion ?? 0,
            template,
            NCLegacyBankJobsIdentity.Command("correlation-bank-binding", sourceKey, sourceRevision),
            AuditSource: auditSource));
        if (advanced.Status is not (NCLegacyBindingMutationStatus.Applied or NCLegacyBindingMutationStatus.Duplicate))
        {
            return Issue(sourceKind, sourceKey,
                advanced.Status == NCLegacyBindingMutationStatus.StorageFailure
                    ? NCLegacyMigrationIssueKind.StorageFailure
                    : NCLegacyMigrationIssueKind.TargetConflict,
                advanced.Error ?? "The bank provenance binding was not advanced.",
                advanced.Status == NCLegacyBindingMutationStatus.StorageFailure);
        }

        changed |= advanced.Status == NCLegacyBindingMutationStatus.Applied;
        return new RowOutcome(changed ? RowOutcomeKind.Changed : RowOutcomeKind.Aligned, null);
    }

    private async Task<RowOutcome> VerifyBankProjection(
        NCLegacySourceKind sourceKind,
        string sourceKey,
        string sourceRevision,
        long legacyBalance,
        NCLegacyMigrationBindingRecord? binding,
        NCAccountId accountId)
    {
        if (binding == null)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.MissingProjection,
                "No common-domain provenance binding exists.");
        if (binding.Snapshot.SourceRevision != sourceRevision)
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.SourceChanged,
                "The legacy row changed after its last projection.");

        var account = await _ledger.GetAccountAsync(accountId, true);
        if (account == null || account.Snapshot.Balance != legacyBalance ||
            binding.Snapshot.ProjectedBalance != legacyBalance ||
            BankProjectionHash(account) != binding.Snapshot.ProjectionHash)
        {
            return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.TargetConflict,
                "The common account projection differs from legacy or its binding fingerprint.");
        }
        return new RowOutcome(RowOutcomeKind.Aligned, null);
    }

    private async Task ProcessEmployments(
        NCLegacyMigrationRunMode mode,
        IReadOnlyDictionary<string, DepartmentPrototype?> departments,
        string auditSource,
        List<NCLegacyMigrationIssue> issues,
        CancellationToken cancellation)
    {
        var jobs = BuildJobDepartmentIndex(departments);
        var after = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = await _database.GetNCLegacyEmploymentPageAsync(after, PageSize);
            if (page.Count == 0)
                return;

            foreach (var row in page)
            {
                cancellation.ThrowIfCancellationRequested();
                var sourceKey = $"character:{row.CharacterId.Value}";
                RowOutcome outcome;
                try
                {
                    if (!jobs.TryGetValue(row.JobPrototypeId, out var mapping) || mapping == null ||
                        !_prototypes.TryIndex(new ProtoId<JobPrototype>(row.JobPrototypeId), out JobPrototype? job) ||
                        job == null || job.Salary < 0)
                    {
                        outcome = Issue(
                            NCLegacySourceKind.CharacterEmployment,
                            sourceKey,
                            NCLegacyMigrationIssueKind.InvalidLegacyData,
                            "The legacy job is missing, ambiguous, or has invalid salary policy.");
                    }
                    else
                    {
                        outcome = await ProcessEmploymentProjection(
                            mode,
                            row,
                            mapping,
                            job,
                            auditSource);
                    }
                }
                catch (Exception exception)
                {
                    outcome = Failure(NCLegacySourceKind.CharacterEmployment, sourceKey, exception);
                }
                AddOutcome(issues, outcome);
            }

            after = page[^1].CharacterId.Value;
            if (page.Count < PageSize)
                return;
        }
    }

    private async Task<RowOutcome> ProcessEmploymentProjection(
        NCLegacyMigrationRunMode mode,
        NCLegacyCharacterEmploymentProjection row,
        DepartmentPrototype department,
        JobPrototype job,
        string auditSource)
    {
        var sourceKey = $"character:{row.CharacterId.Value}";
        var employee = NCPartyRef.ForCharacter(row.CharacterId);
        var employer = NCLegacyBankJobsIdentity.OrganizationParty(department.ID);
        var employmentId = NCLegacyBankJobsIdentity.Employment(
            row.CharacterId,
            row.StartedAt,
            row.JobPrototypeId,
            department.ID);
        var status = row.State switch
        {
            NCEmploymentState.Active => NCEmploymentStatus.Active,
            NCEmploymentState.Suspended => NCEmploymentStatus.Suspended,
            NCEmploymentState.Terminated => NCEmploymentStatus.Terminated,
            _ => (NCEmploymentStatus?) null,
        };
        if (status == null)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.InvalidLegacyData, "The legacy employment state is invalid.");

        var intervalTicks = job.Salary > 0
            ? TimeSpan.FromSeconds(Math.Max(1f, _finance.Configuration.PaydayIntervalSeconds)).Ticks
            : 0;
        var employeeAccountId = _finance.GetPrimaryAccountId(employee);
        var employerAccountId = _finance.GetPrimaryAccountId(employer);
        var payrollEmployerAccount = job.Salary > 0 ? employerAccountId : default;
        var payrollEmployeeAccount = job.Salary > 0 ? employeeAccountId : default;
        var desiredIndexId = status == NCEmploymentStatus.Terminated ? default : employmentId;
        var sourceRevision = NCLegacyBankJobsIdentity.Revision(
            row.JobPrototypeId,
            row.State,
            row.StartedAt.Ticks,
            row.UpdatedAt.Ticks,
            department.ID,
            job.Salary,
            intervalTicks);
        var desiredHash = EmploymentProjectionHash(
            employmentId,
            employee,
            employer,
            row.JobPrototypeId,
            status.Value,
            payrollEmployerAccount,
            payrollEmployeeAccount,
            job.Salary,
            intervalTicks,
            desiredIndexId);
        var bindingId = NCLegacyBankJobsIdentity.Binding(
            NCLegacySourceKind.CharacterEmployment.ToString(), sourceKey);
        var binding = await _bindings.GetAsync(bindingId, true);
        if (binding != null &&
            (binding.Snapshot.SourceKind != NCLegacySourceKind.CharacterEmployment ||
             binding.Snapshot.SourceKey != sourceKey || binding.Snapshot.PrimaryParty != employee ||
             binding.Snapshot.RelatedParty != employer))
        {
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.TargetConflict,
                "The employment provenance binding points to different parties.");
        }

        if (mode == NCLegacyMigrationRunMode.Verify)
            return await VerifyEmploymentProjection(sourceKey, sourceRevision, desiredHash, employmentId, binding);

        if (binding != null)
        {
            var boundRecord = await _employment.GetAsync(binding.Snapshot.EmploymentId, true);
            var boundIndex = await _employment.GetEmployeeIndexAsync(employee, true);
            var matchesPreviousProjection = boundRecord != null &&
                                            EmploymentProjectionHash(boundRecord, boundIndex) ==
                                            binding.Snapshot.ProjectionHash;
            // Terminating the old tenure and projecting its replacement are separate domain commits. A persisted
            // deterministic change receipt is the only accepted proof that a mismatching old projection is our own
            // interrupted release, rather than gameplay or administrative activity that must stop the migration.
            var resumesReleasedEmployment = !matchesPreviousProjection &&
                                             binding.Snapshot.SourceRevision != sourceRevision &&
                                             boundRecord?.Snapshot.Status == NCEmploymentStatus.Terminated &&
                                             await HasCompletedEmploymentChange(
                                                 "terminate-previous-employment",
                                                 sourceKey,
                                                 sourceRevision,
                                                 binding.Snapshot.EmploymentId);
            if (!matchesPreviousProjection && !resumesReleasedEmployment)
            {
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict,
                    "The common employment changed after its last imported projection.");
            }
            if (binding.Snapshot.SourceRevision == sourceRevision)
            {
                return binding.Snapshot.EmploymentId == employmentId && binding.Snapshot.ProjectionHash == desiredHash
                    ? new RowOutcome(RowOutcomeKind.Aligned, null)
                    : Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                        NCLegacyMigrationIssueKind.TargetConflict,
                        "The binding revision matches but the employment projection differs.");
            }
        }

        var employerAccount = await _finance.EnsureLegacyOrganizationAsync(
            department.ID,
            Loc.GetString(department.Name),
            auditSource);
        var employeeAccount = await _finance.EnsurePrimaryAccountAsync(employee, auditSource);
        if (!employerAccount.IsResolved || !employeeAccount.IsResolved)
        {
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.StorageFailure,
                employerAccount.Error ?? employeeAccount.Error ?? "A payroll account is unavailable.", true);
        }

        var changed = false;
        var index = await _employment.GetEmployeeIndexAsync(employee, true);
        if (index?.Snapshot.CurrentEmploymentId.IsValid == true &&
            index.Snapshot.CurrentEmploymentId != employmentId)
        {
            if (binding == null || index.Snapshot.CurrentEmploymentId != binding.Snapshot.EmploymentId)
            {
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict,
                    "A non-migration employment currently owns this character.");
            }

            var previous = await _employment.GetAsync(index.Snapshot.CurrentEmploymentId, true);
            if (previous == null)
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict, "The current imported employment cannot be loaded.");
            var release = await _employment.ChangeAsync(new NCChangeEmploymentRequest(
                NCLegacyBankJobsIdentity.Command("terminate-previous-employment", sourceKey, sourceRevision),
                previous.Snapshot.EmploymentId,
                previous.AggregateVersion,
                index.AggregateVersion,
                previous.Snapshot.PositionId,
                NCEmploymentStatus.Terminated,
                previous.Snapshot.EmployerAccountId,
                previous.Snapshot.PayeeAccountId,
                previous.Snapshot.SalaryAmount,
                previous.Snapshot.PayrollIntervalTicks,
                employer,
                NCLegacyBankJobsIdentity.Command("correlation-terminate-employment", sourceKey, sourceRevision),
                AuditSource: auditSource));
            if (release.Status is not (NCEmploymentMutationStatus.Applied or NCEmploymentMutationStatus.Duplicate))
                return EmploymentMutationFailure(sourceKey, release);
            changed |= release.Status == NCEmploymentMutationStatus.Applied;
            index = await _employment.GetEmployeeIndexAsync(employee, true);
        }

        var record = await _employment.GetAsync(employmentId, true);
        if (record == null)
        {
            if (index?.Snapshot.CurrentEmploymentId.IsValid == true)
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict,
                    "The per-character index is occupied before the desired hire.");

            var hire = await _employment.HireAsync(new NCHirePartyRequest(
                NCLegacyBankJobsIdentity.Command("hire-employment-projection", sourceKey, sourceRevision),
                employmentId,
                employee,
                employer,
                row.JobPrototypeId,
                index?.AggregateVersion ?? 0,
                payrollEmployerAccount,
                payrollEmployeeAccount,
                job.Salary,
                intervalTicks,
                employer,
                NCLegacyBankJobsIdentity.Command("correlation-hire-employment", sourceKey, sourceRevision),
                AuditSource: auditSource));
            if (hire.Status is not (NCEmploymentMutationStatus.Applied or NCEmploymentMutationStatus.Duplicate))
                return EmploymentMutationFailure(sourceKey, hire);
            changed |= hire.Status == NCEmploymentMutationStatus.Applied;
            record = await _employment.GetAsync(employmentId, true);
            index = await _employment.GetEmployeeIndexAsync(employee, true);
        }

        if (record == null || record.Snapshot.Employee != employee || record.Snapshot.Employer != employer)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.TargetConflict,
                "The desired employment ID is owned by another relation.");

        var needsChange = record.Snapshot.PositionId != row.JobPrototypeId || record.Snapshot.Status != status ||
                          record.Snapshot.EmployerAccountId != payrollEmployerAccount ||
                          record.Snapshot.PayeeAccountId != payrollEmployeeAccount ||
                          record.Snapshot.SalaryAmount != job.Salary ||
                          record.Snapshot.PayrollIntervalTicks != intervalTicks;
        if (needsChange)
        {
            if (record.Snapshot.Status == NCEmploymentStatus.Terminated)
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict,
                    "A terminated deterministic employment cannot be reopened.");
            if (index == null || index.Snapshot.CurrentEmploymentId != employmentId)
                return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                    NCLegacyMigrationIssueKind.TargetConflict,
                    "The desired employment does not own the per-character index.");

            var change = await _employment.ChangeAsync(new NCChangeEmploymentRequest(
                NCLegacyBankJobsIdentity.Command("change-employment-projection", sourceKey, sourceRevision),
                employmentId,
                record.AggregateVersion,
                index.AggregateVersion,
                row.JobPrototypeId,
                status.Value,
                payrollEmployerAccount,
                payrollEmployeeAccount,
                job.Salary,
                intervalTicks,
                employer,
                NCLegacyBankJobsIdentity.Command("correlation-change-employment", sourceKey, sourceRevision),
                AuditSource: auditSource));
            if (change.Status is not (NCEmploymentMutationStatus.Applied or NCEmploymentMutationStatus.Duplicate))
                return EmploymentMutationFailure(sourceKey, change);
            changed |= change.Status == NCEmploymentMutationStatus.Applied;
        }

        record = await _employment.GetAsync(employmentId, true);
        index = await _employment.GetEmployeeIndexAsync(employee, true);
        if (record == null || EmploymentProjectionHash(record, index) != desiredHash)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.TargetConflict,
                "The employment projection differs after deterministic replay.");

        var template = new NCLegacyMigrationBindingSnapshot(
            bindingId,
            NCLegacySourceKind.CharacterEmployment,
            sourceKey,
            sourceRevision,
            desiredHash,
            0,
            employee,
            employer,
            employeeAccountId,
            employmentId,
            default,
            default,
            binding?.Snapshot.DataVersion ?? 0,
            default);
        var advanced = await _bindings.AdvanceAsync(new NCAdvanceLegacyMigrationBindingRequest(
            NCLegacyBankJobsIdentity.Command("advance-employment-binding", sourceKey, sourceRevision),
            binding?.AggregateVersion ?? 0,
            template,
            NCLegacyBankJobsIdentity.Command("correlation-employment-binding", sourceKey, sourceRevision),
            AuditSource: auditSource));
        if (advanced.Status is not (NCLegacyBindingMutationStatus.Applied or NCLegacyBindingMutationStatus.Duplicate))
        {
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                advanced.Status == NCLegacyBindingMutationStatus.StorageFailure
                    ? NCLegacyMigrationIssueKind.StorageFailure
                    : NCLegacyMigrationIssueKind.TargetConflict,
                advanced.Error ?? "The employment provenance binding was not advanced.",
                advanced.Status == NCLegacyBindingMutationStatus.StorageFailure);
        }
        changed |= advanced.Status == NCLegacyBindingMutationStatus.Applied;
        return new RowOutcome(changed ? RowOutcomeKind.Changed : RowOutcomeKind.Aligned, null);
    }

    private async Task<RowOutcome> VerifyEmploymentProjection(
        string sourceKey,
        string sourceRevision,
        string desiredHash,
        NCEmploymentId employmentId,
        NCLegacyMigrationBindingRecord? binding)
    {
        if (binding == null)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.MissingProjection, "No common employment provenance binding exists.");
        if (binding.Snapshot.SourceRevision != sourceRevision)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.SourceChanged,
                "The legacy employment changed after its last projection.");
        if (binding.Snapshot.EmploymentId != employmentId || binding.Snapshot.ProjectionHash != desiredHash)
            return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.TargetConflict,
                "The binding points to another employment projection.");

        var record = await _employment.GetAsync(employmentId, true);
        var index = record == null
            ? null
            : await _employment.GetEmployeeIndexAsync(record.Snapshot.Employee, true);
        return record != null && EmploymentProjectionHash(record, index) == desiredHash
            ? new RowOutcome(RowOutcomeKind.Aligned, null)
            : Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
                NCLegacyMigrationIssueKind.TargetConflict,
                "The common employment differs from its legacy projection.");
    }

    private static string BankProjectionHash(NCAccountRecord account)
    {
        var snapshot = account.Snapshot;
        return NCLegacyBankJobsIdentity.Revision(
            snapshot.Owner,
            snapshot.Kind,
            snapshot.CurrencyId,
            snapshot.Balance,
            snapshot.AllowsNegativeBalance,
            snapshot.Status);
    }

    private static string EmploymentProjectionHash(
        NCEmploymentRecord record,
        NCPartyEmploymentIndexRecord? index)
    {
        var snapshot = record.Snapshot;
        return EmploymentProjectionHash(
            snapshot.EmploymentId,
            snapshot.Employee,
            snapshot.Employer,
            snapshot.PositionId,
            snapshot.Status,
            snapshot.EmployerAccountId,
            snapshot.PayeeAccountId,
            snapshot.SalaryAmount,
            snapshot.PayrollIntervalTicks,
            index?.Snapshot.CurrentEmploymentId ?? default);
    }

    private static string EmploymentProjectionHash(
        NCEmploymentId employmentId,
        NCPartyRef employee,
        NCPartyRef employer,
        string positionId,
        NCEmploymentStatus status,
        NCAccountId employerAccountId,
        NCAccountId payeeAccountId,
        long salary,
        long intervalTicks,
        NCEmploymentId indexEmploymentId)
    {
        return NCLegacyBankJobsIdentity.Revision(
            employmentId,
            employee,
            employer,
            positionId,
            status,
            employerAccountId,
            payeeAccountId,
            salary,
            intervalTicks,
            indexEmploymentId);
    }

    private async Task<bool> HasCompletedEmploymentChange(
        string operation,
        string sourceKey,
        string sourceRevision,
        NCEmploymentId employmentId)
    {
        var commandId = NCLegacyBankJobsIdentity.Command(operation, sourceKey, sourceRevision);
        var receipt = await _database.GetNCPersistentCommandResultAsync(commandId);
        return receipt is
        {
            CommandType: "living-city.employment.change",
            ScopeType: "employment",
            Status: NCPersistentMutationStatus.DuplicateCompleted,
        } && receipt.ScopeId == employmentId.Value.ToString("D");
    }

    private IReadOnlyDictionary<string, DepartmentPrototype?> BuildDepartmentIndex()
    {
        var result = new Dictionary<string, DepartmentPrototype?>(StringComparer.Ordinal);
        foreach (var department in _prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!department.NCSelectable)
                continue;
            result[department.ID] = department;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, DepartmentPrototype?> BuildJobDepartmentIndex(
        IReadOnlyDictionary<string, DepartmentPrototype?> departments)
    {
        var result = new Dictionary<string, DepartmentPrototype?>(StringComparer.Ordinal);
        foreach (var department in departments.Values.Where(static value => value != null))
        {
            foreach (var job in department!.Roles)
            {
                if (!result.TryAdd(job.Id, department))
                    result[job.Id] = null;
            }
        }
        return result;
    }

    private static RowOutcome EmploymentMutationFailure(string sourceKey, NCEmploymentMutationResult result)
    {
        return Issue(NCLegacySourceKind.CharacterEmployment, sourceKey,
            result.Status == NCEmploymentMutationStatus.StorageFailure
                ? NCLegacyMigrationIssueKind.StorageFailure
                : NCLegacyMigrationIssueKind.TargetConflict,
            result.Error ?? "The employment mutation was rejected.",
            result.Status == NCEmploymentMutationStatus.StorageFailure);
    }

    private void AddOutcome(List<NCLegacyMigrationIssue> issues, RowOutcome outcome)
    {
        lock (_runLock)
        {
            _progress = outcome.Kind switch
            {
                RowOutcomeKind.Aligned => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Aligned = _progress.Aligned + 1,
                },
                RowOutcomeKind.Changed => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Changed = _progress.Changed + 1,
                },
                RowOutcomeKind.Conflict => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Conflicts = _progress.Conflicts + 1,
                    LastError = outcome.Issue?.Message,
                },
                _ => _progress with
                {
                    Processed = _progress.Processed + 1,
                    Failed = _progress.Failed + 1,
                    LastError = outcome.Issue?.Message,
                },
            };
        }

        if (outcome.Issue != null && issues.Count < MaximumIssues)
            issues.Add(outcome.Issue);
    }

    private void AddRunFailure(
        List<NCLegacyMigrationIssue> issues,
        NCLegacySourceKind sourceKind,
        string sourceKey,
        string message)
    {
        lock (_runLock)
        {
            _progress = _progress with
            {
                Failed = _progress.Failed + 1,
                LastError = message,
            };
        }

        if (issues.Count < MaximumIssues)
        {
            issues.Add(new NCLegacyMigrationIssue(
                sourceKind,
                sourceKey,
                NCLegacyMigrationIssueKind.StorageFailure,
                message));
        }
    }

    private static NCLegacySourceKind SourceKindForPhase(NCLegacyMigrationPhase phase)
    {
        return phase switch
        {
            NCLegacyMigrationPhase.OrganizationBank => NCLegacySourceKind.OrganizationBank,
            NCLegacyMigrationPhase.CharacterEmployment => NCLegacySourceKind.CharacterEmployment,
            _ => NCLegacySourceKind.CharacterBank,
        };
    }

    private void SetPhase(NCLegacyMigrationPhase phase)
    {
        lock (_runLock)
            _progress = _progress with { Phase = phase };
    }

    private NCLegacyBankJobsMigrationReport BuildReport(
        NCLegacyMigrationRunStatus status,
        NCLegacyMigrationRunMode mode,
        IReadOnlyList<NCLegacyMigrationIssue> issues)
    {
        var progress = Progress;
        return new NCLegacyBankJobsMigrationReport(
            status,
            mode,
            progress.Processed,
            progress.Aligned,
            progress.Changed,
            progress.Conflicts,
            progress.Failed,
            issues.ToArray());
    }

    private static NCLegacyBankJobsMigrationReport EmptyReport(
        NCLegacyMigrationRunStatus status,
        NCLegacyMigrationRunMode mode,
        string error)
    {
        return new(status, mode, 0, 0, 0, 0, 0,
            [new NCLegacyMigrationIssue(
                NCLegacySourceKind.CharacterBank,
                "run",
                NCLegacyMigrationIssueKind.InvalidLegacyData,
                error)]);
    }

    private static RowOutcome Issue(
        NCLegacySourceKind sourceKind,
        string sourceKey,
        NCLegacyMigrationIssueKind kind,
        string message,
        bool failed = false)
    {
        return new RowOutcome(
            failed ? RowOutcomeKind.Failed : RowOutcomeKind.Conflict,
            new NCLegacyMigrationIssue(sourceKind, sourceKey, kind, message));
    }

    private static RowOutcome Failure(NCLegacySourceKind sourceKind, string sourceKey, Exception exception)
    {
        return Issue(sourceKind, sourceKey, NCLegacyMigrationIssueKind.StorageFailure,
            exception.Message, true);
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

    private enum RowOutcomeKind : byte
    {
        Aligned,
        Changed,
        Conflict,
        Failed,
    }

    private sealed record RowOutcome(RowOutcomeKind Kind, NCLegacyMigrationIssue? Issue);
}
