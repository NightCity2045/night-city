using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task SyncNCOrganizationsAsync(
        IReadOnlyCollection<NCOrganizationDefinition> organizations,
        IReadOnlyCollection<NCDepartmentDefinition> departments,
        IReadOnlyCollection<NCPositionDefinition> positions);

    Task<NCEmploymentResult> ApplyNCEmploymentActionAsync(NCEmploymentMutation mutation);
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
}

public abstract partial class ServerDbBase
{
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
        await transaction.CommitAsync();
    }

    public async Task<NCEmploymentResult> ApplyNCEmploymentActionAsync(NCEmploymentMutation mutation)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        if (await db.DbContext.NCEmploymentHistory.AnyAsync(entry => entry.RequestId == mutation.RequestId))
            return new NCEmploymentResult(false, "nc-employment-error-duplicate-request", null);

        var current = await db.DbContext.NCCharacterEmployment
            .SingleOrDefaultAsync(entry => entry.ProfileId == mutation.TargetProfileId);
        var targetPosition = mutation.PositionId == null
            ? null
            : await db.DbContext.NCPosition.FindAsync(mutation.PositionId.Value);

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

        if (mutation.ActorProfileId != null)
        {
            var actor = await db.DbContext.NCCharacterEmployment
                .SingleOrDefaultAsync(entry => entry.ProfileId == mutation.ActorProfileId.Value);
            var actorPosition = actor == null ? null : await db.DbContext.NCPosition.FindAsync(actor.PositionId);
            if (actor == null || actor.EmploymentState != NCEmploymentState.Active ||
                actor.OrganizationId != mutation.OrganizationId || actorPosition == null ||
                !CanPerform(actorPosition, mutation.Action, targetPosition))
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
                current.EmploymentState = NCEmploymentState.SuspendedUnpaid;
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

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCEmploymentResult(true, null, current);
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
