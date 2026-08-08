// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Content.Shared.Database._NC.Police;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCPoliceFineData(
    long Id, NCCharacterId TargetCharacterId, string TargetName, string Article, string Reason,
    int Amount, NCPoliceFineStatus Status, string IssuedByName, DateTime IssuedAt,
    DateTime DueAt, DateTime UpdatedAt, DateTime? PaidAt);

public sealed record NCPoliceFinePaymentData(NCPoliceFinePaymentResult Result, int? Balance);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPoliceFineData>> GetNCPoliceFinesAsync(int limit, NCCharacterId? target = null);
    Task<NCPoliceFineData?> CreateNCPoliceFineAsync(NCCharacterId target, string article, string reason,
        int amount, DateTime dueAt, NCCharacterId actorCharacterId, string actorName);
    Task<NCPoliceFineData?> SetNCPoliceFineStatusAsync(long fineId, NCPoliceFineStatus status,
        string reason, NCCharacterId actorCharacterId, string actorName);
    Task<NCPoliceFinePaymentData> PayNCPoliceFineAsync(long fineId, NCCharacterId payer);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPoliceFineData>> GetNCPoliceFinesAsync(int limit, NCCharacterId? target = null)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceFinesAsync(limit, target));
    }

    public Task<NCPoliceFineData?> CreateNCPoliceFineAsync(NCCharacterId target, string article, string reason,
        int amount, DateTime dueAt, NCCharacterId actorCharacterId, string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCPoliceFineAsync(target, article, reason, amount, dueAt,
            actorCharacterId, actorName));
    }

    public Task<NCPoliceFineData?> SetNCPoliceFineStatusAsync(long fineId, NCPoliceFineStatus status,
        string reason, NCCharacterId actorCharacterId, string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPoliceFineStatusAsync(fineId, status, reason,
            actorCharacterId, actorName));
    }

    public Task<NCPoliceFinePaymentData> PayNCPoliceFineAsync(long fineId, NCCharacterId payer)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.PayNCPoliceFineAsync(fineId, payer));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCPoliceFineData>> GetNCPoliceFinesAsync(int limit, NCCharacterId? target = null)
    {
        await using var db = await GetDb();
        var query = db.DbContext.NCPoliceFines.AsNoTracking();
        if (target is { IsValid: true } characterId)
            query = query.Where(value => value.TargetProfileId == characterId.Value);
        var fines = await query.OrderBy(value => value.Status == NCPoliceFineStatus.Paid || value.Status == NCPoliceFineStatus.Voided)
            .ThenByDescending(value => value.IssuedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync();
        return fines.Select(ToFineData).ToList();
    }

    public async Task<NCPoliceFineData?> CreateNCPoliceFineAsync(NCCharacterId target, string article, string reason,
        int amount, DateTime dueAt, NCCharacterId actorCharacterId, string actorName)
    {
        article = article.Trim(); reason = reason.Trim(); actorName = actorName.Trim();
        if (!target.IsValid || !actorCharacterId.IsValid || amount <= 0 || dueAt <= DateTime.UtcNow ||
            article.Length is < 1 or > 128 || reason.Length is < 1 or > 512 || actorName.Length is < 1 or > 128)
            return null;
        await using var db = await GetDb();
        var profile = await db.DbContext.Profile.AsNoTracking().SingleOrDefaultAsync(value => value.Id == target.Value);
        if (profile == null) return null;
        var now = DateTime.UtcNow;
        var fine = new NCPoliceFine
        {
            TargetProfileId = profile.Id, TargetName = profile.CharacterName, Article = article, Reason = reason,
            Amount = amount, Status = NCPoliceFineStatus.Issued, IssuedByProfileId = actorCharacterId.Value,
            IssuedByName = actorName, IssuedAt = now, DueAt = dueAt, UpdatedAt = now,
        };
        fine.Events.Add(new NCPoliceFineEvent
        {
            EventType = NCPoliceFineEventType.Issued, PreviousStatus = NCPoliceFineStatus.Issued,
            NewStatus = NCPoliceFineStatus.Issued, Reason = reason, ActorProfileId = actorCharacterId.Value,
            ActorName = actorName, CreatedAt = now,
        });
        db.DbContext.NCPoliceFines.Add(fine);
        await db.DbContext.SaveChangesAsync();
        return ToFineData(fine);
    }

    public async Task<NCPoliceFineData?> SetNCPoliceFineStatusAsync(long fineId, NCPoliceFineStatus status,
        string reason, NCCharacterId actorCharacterId, string actorName)
    {
        reason = reason.Trim(); actorName = actorName.Trim();
        if (fineId <= 0 || !actorCharacterId.IsValid || !Enum.IsDefined(status) ||
            status is NCPoliceFineStatus.Issued or NCPoliceFineStatus.Paid || reason.Length is < 1 or > 512)
            return null;
        await using var db = await GetDb();
        var fine = await db.DbContext.NCPoliceFines.Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.Id == fineId);
        if (fine == null || fine.Status is NCPoliceFineStatus.Paid or NCPoliceFineStatus.Voided)
            return null;
        var previous = fine.Status;
        var now = DateTime.UtcNow;
        fine.Status = status; fine.UpdatedAt = now;
        fine.Events.Add(new NCPoliceFineEvent
        {
            EventType = NCPoliceFineEventType.StatusChanged, PreviousStatus = previous, NewStatus = status,
            Reason = reason, ActorProfileId = actorCharacterId.Value, ActorName = actorName, CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        return ToFineData(fine);
    }

    public async Task<NCPoliceFinePaymentData> PayNCPoliceFineAsync(long fineId, NCCharacterId payer)
    {
        if (fineId <= 0 || !payer.IsValid)
            return new(NCPoliceFinePaymentResult.FineNotFound, null);
        await using var db = await GetDb();
        var fine = await db.DbContext.NCPoliceFines.Include(value => value.Events)
            .SingleOrDefaultAsync(value => value.Id == fineId);
        if (fine == null) return new(NCPoliceFinePaymentResult.FineNotFound, null);
        if (fine.TargetProfileId != payer.Value) return new(NCPoliceFinePaymentResult.NotOwner, null);
        if (fine.Status is not (NCPoliceFineStatus.Issued or NCPoliceFineStatus.Overdue))
            return new(NCPoliceFinePaymentResult.NotPayable, null);
        var account = await db.DbContext.NCCharacterBankAccounts.SingleOrDefaultAsync(value => value.ProfileId == payer.Value);
        if (account == null) return new(NCPoliceFinePaymentResult.AccountNotFound, null);
        if (account.Balance < fine.Amount) return new(NCPoliceFinePaymentResult.InsufficientFunds, account.Balance);
        var now = DateTime.UtcNow;
        var previous = fine.Status;
        account.Balance -= fine.Amount; account.UpdatedAt = now;
        fine.Status = NCPoliceFineStatus.Paid; fine.PaidAt = now; fine.UpdatedAt = now;
        fine.Events.Add(new NCPoliceFineEvent
        {
            EventType = NCPoliceFineEventType.Paid, PreviousStatus = previous, NewStatus = NCPoliceFineStatus.Paid,
            Reason = "Paid through bank account", ActorProfileId = payer.Value, ActorName = fine.TargetName, CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        return new(NCPoliceFinePaymentResult.Success, account.Balance);
    }

    private static NCPoliceFineData ToFineData(NCPoliceFine value) => new(value.Id,
        new NCCharacterId(value.TargetProfileId), value.TargetName, value.Article, value.Reason, value.Amount,
        value.Status, value.IssuedByName, value.IssuedAt, value.DueAt, value.UpdatedAt, value.PaidAt);
}
