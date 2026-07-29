// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task SyncNCOrganizationsAsync(
        IReadOnlyCollection<NCOrganizationDefinition> organizations,
        IReadOnlyCollection<NCDepartmentDefinition> departments,
        IReadOnlyCollection<NCPositionDefinition> positions);

    Task<NCEmploymentResult> ApplyNCEmploymentActionAsync(NCEmploymentMutation mutation);
    Task<IReadOnlyList<NCEmploymentHistory>> GetNCEmploymentHistoryAsync(
        int profileId,
        int limit);
}

public sealed partial class ServerDbManager
{
    public Task SyncNCOrganizationsAsync(
        IReadOnlyCollection<NCOrganizationDefinition> organizations,
        IReadOnlyCollection<NCDepartmentDefinition> departments,
        IReadOnlyCollection<NCPositionDefinition> positions)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SyncNCOrganizationsAsync(organizations, departments, positions));
    }

    public Task<NCEmploymentResult> ApplyNCEmploymentActionAsync(NCEmploymentMutation mutation)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ApplyNCEmploymentActionAsync(mutation));
    }

    public Task<IReadOnlyList<NCEmploymentHistory>> GetNCEmploymentHistoryAsync(
        int profileId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCEmploymentHistoryAsync(profileId, limit));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCEmploymentHistory>> GetNCEmploymentHistoryAsync(
        int profileId,
        int limit)
    {
        await using var db = await GetDb();
        return await db.DbContext.NCEmploymentHistory
            .AsNoTracking()
            .Where(entry => entry.ProfileId == profileId)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
    }

    public async Task SyncNCOrganizationsAsync(
        IReadOnlyCollection<NCOrganizationDefinition> organizations,
        IReadOnlyCollection<NCDepartmentDefinition> departments,
        IReadOnlyCollection<NCPositionDefinition> positions)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        // Organizations are synchronized in dependency order because positions reference them.
        foreach (var definition in organizations)
        {
            var row = await db.DbContext.NCOrganization.FindAsync(definition.OrganizationId);
            row ??= new NCOrganization { OrganizationId = definition.OrganizationId };
            if (db.DbContext.Entry(row).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.DbContext.NCOrganization.Add(row);

            row.PrototypeId = definition.PrototypeId;
            row.Name = definition.Name;
            row.Status = NCOrganizationStatus.Active;
            row.DefaultEntryPositionId = null;

            if (definition.HasPayrollAccount && row.BankAccountId == null)
            {
                var accountId = Guid.NewGuid();
                db.DbContext.NCBankAccount.Add(new NCBankAccount
                {
                    BankAccountId = accountId,
                    AccountNumber = $"ORG{definition.OrganizationId:N}"[..32],
                    AccountType = NCBankAccountType.Organization,
                    CurrencyPrototypeId = definition.CurrencyPrototypeId,
                    Balance = definition.PayrollStartingBalance,
                    Status = NCBankAccountStatus.Active,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                row.BankAccountId = accountId;
            }
        }

        await db.DbContext.SaveChangesAsync();

        foreach (var definition in departments)
        {
            var row = await db.DbContext.NCDepartment.FindAsync(definition.DepartmentId);
            row ??= new NCDepartment { DepartmentId = definition.DepartmentId };
            if (db.DbContext.Entry(row).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.DbContext.NCDepartment.Add(row);

            row.OrganizationId = definition.OrganizationId;
            row.PrototypeId = definition.PrototypeId;
            row.Name = definition.Name;
        }

        foreach (var definition in positions)
        {
            var row = await db.DbContext.NCPosition.FindAsync(definition.PositionId);
            row ??= new NCPosition { PositionId = definition.PositionId };
            if (db.DbContext.Entry(row).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.DbContext.NCPosition.Add(row);

            row.OrganizationId = definition.OrganizationId;
            row.DepartmentId = definition.DepartmentId;
            row.PrototypeId = definition.PrototypeId;
            row.Name = definition.Name;
            row.RankWeight = definition.RankWeight;
            row.BaseSalary = definition.BaseSalary;
            row.PayIntervalSeconds = definition.PayIntervalSeconds;
            row.PayrollAccountId = (await db.DbContext.NCOrganization
                .FindAsync(definition.OrganizationId))?.BankAccountId;
            row.IsLeadership = definition.IsLeadership;
            row.CanHire = definition.CanHire;
            row.CanPromote = definition.CanPromote;
            row.CanDemote = definition.CanDemote;
            row.CanTransfer = definition.CanTransfer;
            row.CanSuspend = definition.CanSuspend;
            row.CanDismiss = definition.CanDismiss;
            row.MaxPromotableRankWeight = definition.MaxPromotableRankWeight;
        }

        await db.DbContext.SaveChangesAsync();

        foreach (var definition in organizations)
        {
            var row = await db.DbContext.NCOrganization.FindAsync(definition.OrganizationId);
            if (row != null)
                row.DefaultEntryPositionId = definition.DefaultEntryPositionId;
        }

        await db.DbContext.SaveChangesAsync();

        // Missing YAML is never treated as a hard delete: durable history keeps the old rows,
        // while affected employment is explicitly invalidated so stale access cannot survive.
        var activeOrganizationIds = organizations.Select(entry => entry.OrganizationId).ToHashSet();
        var activePositionIds = positions.Select(entry => entry.PositionId).ToHashSet();
        var archivedOrganizations = await db.DbContext.NCOrganization
            .Where(entry => !activeOrganizationIds.Contains(entry.OrganizationId))
            .ToListAsync();
        foreach (var archived in archivedOrganizations)
            archived.Status = NCOrganizationStatus.Archived;

        var invalidEmployments = await db.DbContext.NCCharacterEmployment
            .Where(entry =>
                entry.EmploymentState != NCEmploymentState.Terminated &&
                (!activeOrganizationIds.Contains(entry.OrganizationId) ||
                 !activePositionIds.Contains(entry.PositionId)))
            .ToListAsync();
        var invalidatedAt = DateTime.UtcNow;
        foreach (var employment in invalidEmployments)
        {
            employment.EmploymentState = NCEmploymentState.Invalid;
            employment.UpdatedAt = invalidatedAt;
            employment.Version++;
            db.DbContext.NCEmploymentHistory.Add(new NCEmploymentHistory
            {
                ProfileId = employment.ProfileId,
                OrganizationId = employment.OrganizationId,
                OldDepartmentId = employment.DepartmentId,
                NewDepartmentId = employment.DepartmentId,
                OldPositionId = employment.PositionId,
                NewPositionId = employment.PositionId,
                Action = NCEmploymentAction.AdministrativeChange,
                Reason = "prototype-archived",
                Timestamp = invalidatedAt,
                RequestId = Guid.NewGuid(),
            });
        }

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task<NCEmploymentResult> ApplyNCEmploymentActionAsync(NCEmploymentMutation mutation)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        if (string.IsNullOrWhiteSpace(mutation.Reason) || mutation.Reason.Length > 512)
            return new NCEmploymentResult(false, "nc-employment-error-invalid-reason", null);
        if (await db.DbContext.NCEmploymentHistory.AnyAsync(entry => entry.RequestId == mutation.RequestId))
            return new NCEmploymentResult(false, "nc-employment-error-duplicate-request", null);
        if (!await db.DbContext.NCCharacterLifecycle.AnyAsync(entry =>
                entry.ProfileId == mutation.TargetProfileId &&
                entry.Status == NCCharacterLifecycleStatus.Alive))
        {
            return new NCEmploymentResult(false, "nc-employment-error-target-unavailable", null);
        }

        var current = await db.DbContext.NCCharacterEmployment
            .SingleOrDefaultAsync(entry => entry.ProfileId == mutation.TargetProfileId);
        var organization = await db.DbContext.NCOrganization.FindAsync(mutation.OrganizationId);
        var targetPosition = mutation.PositionId == null
            ? null
            : await db.DbContext.NCPosition.FindAsync(mutation.PositionId.Value);
        var currentPosition = current == null
            ? null
            : await db.DbContext.NCPosition.FindAsync(current.PositionId);

        if (mutation.ExpectedVersion != null &&
            (current?.Version ?? 0) != mutation.ExpectedVersion.Value)
            return new NCEmploymentResult(false, "nc-employment-error-conflict", current);

        if (organization is not { Status: NCOrganizationStatus.Active })
            return new NCEmploymentResult(false, "nc-employment-error-invalid-organization", null);
        if (mutation.Action is NCEmploymentAction.Hire or NCEmploymentAction.Promote or
            NCEmploymentAction.Demote or NCEmploymentAction.Transfer)
        {
            if (targetPosition == null || targetPosition.OrganizationId != mutation.OrganizationId)
                return new NCEmploymentResult(false, "nc-employment-error-invalid-position", null);
        }

        if (mutation.Action == NCEmploymentAction.Hire && current is { EmploymentState: not NCEmploymentState.Terminated })
            return new NCEmploymentResult(false, "nc-employment-error-already-employed", null);
        if (mutation.Action != NCEmploymentAction.Hire && current == null)
            return new NCEmploymentResult(false, "nc-employment-error-not-employed", null);
        if (mutation.Action != NCEmploymentAction.Hire &&
            current!.OrganizationId != mutation.OrganizationId)
        {
            return new NCEmploymentResult(false, "nc-employment-error-wrong-organization", null);
        }
        if (mutation.ActorProfileId == mutation.TargetProfileId)
            return new NCEmploymentResult(false, "nc-employment-error-self-action", null);
        if (mutation.ActorProfileId != null &&
            mutation.Action == NCEmploymentAction.Hire &&
            targetPosition?.PositionId != organization.DefaultEntryPositionId)
        {
            return new NCEmploymentResult(false, "nc-employment-error-entry-position-required", null);
        }
        if (mutation.Action == NCEmploymentAction.Promote &&
            (currentPosition == null || targetPosition!.RankWeight <= currentPosition.RankWeight))
        {
            return new NCEmploymentResult(false, "nc-employment-error-not-promotion", null);
        }
        if (mutation.Action == NCEmploymentAction.Demote &&
            (currentPosition == null || targetPosition!.RankWeight >= currentPosition.RankWeight))
        {
            return new NCEmploymentResult(false, "nc-employment-error-not-demotion", null);
        }
        if (mutation.Action == NCEmploymentAction.Suspend &&
            current!.EmploymentState != NCEmploymentState.Active)
        {
            return new NCEmploymentResult(false, "nc-employment-error-invalid-state", null);
        }
        if (mutation.Action == NCEmploymentAction.Reinstate &&
            current!.EmploymentState is not (NCEmploymentState.SuspendedPaid or
                NCEmploymentState.SuspendedUnpaid))
        {
            return new NCEmploymentResult(false, "nc-employment-error-invalid-state", null);
        }
        if (mutation.Action == NCEmploymentAction.Dismiss &&
            current!.EmploymentState == NCEmploymentState.Terminated)
        {
            return new NCEmploymentResult(false, "nc-employment-error-invalid-state", null);
        }

        if (mutation.ActorProfileId != null)
        {
            var actor = await db.DbContext.NCCharacterEmployment
                .SingleOrDefaultAsync(entry => entry.ProfileId == mutation.ActorProfileId.Value);
            var actorPosition = actor == null ? null : await db.DbContext.NCPosition.FindAsync(actor.PositionId);
            if (actor == null || actor.EmploymentState != NCEmploymentState.Active ||
                actor.OrganizationId != mutation.OrganizationId || actorPosition == null ||
                !CanPerform(actorPosition, mutation.Action, targetPosition ?? currentPosition))
            {
                return new NCEmploymentResult(false, "nc-employment-error-forbidden", null);
            }
        }

        var now = DateTime.UtcNow;
        var oldPosition = current?.PositionId;
        var oldDepartment = current?.DepartmentId;
        current ??= new NCCharacterEmployment
        {
            ProfileId = mutation.TargetProfileId,
            HiredAt = now,
            HiredByProfileId = mutation.ActorProfileId,
        };
        if (db.DbContext.Entry(current).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            db.DbContext.NCCharacterEmployment.Add(current);

        switch (mutation.Action)
        {
            case NCEmploymentAction.Hire:
                current.HiredAt = now;
                current.HiredByProfileId = mutation.ActorProfileId;
                current.LastPromotionAt = null;
                current.SuspendedAt = null;
                goto case NCEmploymentAction.Transfer;
            case NCEmploymentAction.Promote:
            case NCEmploymentAction.Demote:
            case NCEmploymentAction.Transfer:
                current.OrganizationId = mutation.OrganizationId;
                current.DepartmentId = targetPosition!.DepartmentId;
                current.PositionId = targetPosition.PositionId;
                current.EmploymentState = NCEmploymentState.Active;
                if (mutation.Action is NCEmploymentAction.Promote or NCEmploymentAction.Demote)
                    current.LastPromotionAt = now;
                break;
            case NCEmploymentAction.Suspend:
                current.EmploymentState = mutation.PaidSuspension
                    ? NCEmploymentState.SuspendedPaid
                    : NCEmploymentState.SuspendedUnpaid;
                current.SuspendedAt = now;
                break;
            case NCEmploymentAction.Reinstate:
                current.EmploymentState = NCEmploymentState.Active;
                current.SuspendedAt = null;
                break;
            case NCEmploymentAction.Dismiss:
                current.EmploymentState = NCEmploymentState.Terminated;
                break;
        }

        current.UpdatedAt = now;
        current.Version++;

        var employmentDocument = await db.DbContext.NCCharacterDocument
            .SingleOrDefaultAsync(entry =>
                entry.ProfileId == mutation.TargetProfileId &&
                entry.DocumentPrototypeId == "NCEmploymentCertificate");
        if (mutation.Action == NCEmploymentAction.Dismiss)
        {
            if (employmentDocument != null)
            {
                employmentDocument.Status = NCLegalRecordStatus.Revoked;
                employmentDocument.RevokedAt = now;
                employmentDocument.Reason = mutation.Reason;
            }
        }
        else
        {
            employmentDocument ??= new NCCharacterDocument
            {
                DocumentId = Guid.NewGuid(),
                ProfileId = mutation.TargetProfileId,
                DocumentPrototypeId = "NCEmploymentCertificate",
                SerialNumber = $"NCEMP{mutation.TargetProfileId:D10}",
            };
            if (db.DbContext.Entry(employmentDocument).State ==
                Microsoft.EntityFrameworkCore.EntityState.Detached)
                db.DbContext.NCCharacterDocument.Add(employmentDocument);
            employmentDocument.Status = NCLegalRecordStatus.Active;
            employmentDocument.IssuedAt = now;
            employmentDocument.RevokedAt = null;
            employmentDocument.IssuedByProfileId = mutation.ActorProfileId;
            employmentDocument.Payload =
                $"organization={current.OrganizationId:N};position={current.PositionId:N};state={current.EmploymentState}";
            employmentDocument.Reason = mutation.Reason;
        }

        db.DbContext.NCEmploymentHistory.Add(new NCEmploymentHistory
        {
            ProfileId = mutation.TargetProfileId,
            OrganizationId = mutation.OrganizationId,
            OldDepartmentId = oldDepartment,
            NewDepartmentId = current.DepartmentId,
            OldPositionId = oldPosition,
            NewPositionId = current.PositionId,
            Action = mutation.Action,
            ActorProfileId = mutation.ActorProfileId,
            ActorAdminId = mutation.ActorAdminId,
            Reason = mutation.Reason,
            RoundId = mutation.RoundId,
            Timestamp = now,
            RequestId = mutation.RequestId,
        });

        try
        {
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCEmploymentResult(true, null, current);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new NCEmploymentResult(false, "nc-employment-error-conflict", null);
        }
    }

    private static bool CanPerform(
        NCPosition actor,
        NCEmploymentAction action,
        NCPosition? target)
    {
        var allowed = action switch
        {
            NCEmploymentAction.Hire => actor.CanHire,
            NCEmploymentAction.Promote => actor.CanPromote,
            NCEmploymentAction.Demote => actor.CanDemote,
            NCEmploymentAction.Transfer => actor.CanTransfer,
            NCEmploymentAction.Suspend or NCEmploymentAction.Reinstate => actor.CanSuspend,
            NCEmploymentAction.Dismiss => actor.CanDismiss,
            _ => false,
        };

        return allowed && (target == null ||
            (target.RankWeight < actor.RankWeight &&
             (actor.MaxPromotableRankWeight == null ||
              target.RankWeight <= actor.MaxPromotableRankWeight)));
    }
}
