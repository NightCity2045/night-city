// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;

namespace Content.Server.Database;

public sealed record NCPersonnelEmployeeData(
    NCCharacterId CharacterId, string CharacterName, string JobPrototypeId, DateTime StartedAt);

public sealed record NCPersonnelCandidateData(NCCharacterId CharacterId, string CharacterName);

public sealed record NCPersonnelHistoryData(
    string? PreviousJobPrototypeId, string? NewJobPrototypeId, NCEmploymentEventType EventType,
    string Reason, string ActorName, DateTime CreatedAt);

public enum NCPersonnelChangeResult : byte
{
    Success,
    CharacterNotFound,
    CharacterUnavailable,
    PositionFull,
    InvalidEmployment,
}

public sealed record NCPersonnelChangeData(NCPersonnelChangeResult Result, NetUserId? TargetUserId);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPersonnelEmployeeData>> GetNCPersonnelRosterAsync(IReadOnlyCollection<string> jobIds);
    Task<IReadOnlyList<NCPersonnelCandidateData>> SearchNCPersonnelCandidatesAsync(string query, int limit);
    Task<IReadOnlyList<NCPersonnelHistoryData>> GetNCPersonnelHistoryAsync(NCCharacterId characterId, int limit);
    Task<string?> GetNCActiveJobAsync(NCCharacterId characterId);
    Task<NCPersonnelChangeData> HireNCCharacterAsync(NCCharacterId target, string jobId, int positionLimit,
        NCCharacterId actor, string actorName, string reason);
    Task<NCPersonnelChangeData> TerminateNCCharacterAsync(NCCharacterId target,
        IReadOnlyCollection<string> organizationJobs, NCCharacterId actor, string actorName, string reason);
    Task<NCPersonnelChangeData> ChangeNCCharacterPositionAsync(NCCharacterId target, string newJobId,
        int positionLimit, IReadOnlyCollection<string> organizationJobs, NCEmploymentEventType eventType,
        NCCharacterId actor, string actorName, string reason);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPersonnelEmployeeData>> GetNCPersonnelRosterAsync(IReadOnlyCollection<string> jobIds)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPersonnelRosterAsync(jobIds));
    }

    public Task<IReadOnlyList<NCPersonnelCandidateData>> SearchNCPersonnelCandidatesAsync(string query, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.SearchNCPersonnelCandidatesAsync(query, limit));
    }

    public Task<IReadOnlyList<NCPersonnelHistoryData>> GetNCPersonnelHistoryAsync(NCCharacterId characterId, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPersonnelHistoryAsync(characterId, limit));
    }

    public Task<string?> GetNCActiveJobAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCActiveJobAsync(characterId));
    }

    public Task<NCPersonnelChangeData> HireNCCharacterAsync(NCCharacterId target, string jobId, int positionLimit,
        NCCharacterId actor, string actorName, string reason)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.HireNCCharacterAsync(target, jobId, positionLimit, actor, actorName, reason));
    }

    public Task<NCPersonnelChangeData> TerminateNCCharacterAsync(NCCharacterId target,
        IReadOnlyCollection<string> organizationJobs, NCCharacterId actor, string actorName, string reason)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TerminateNCCharacterAsync(target, organizationJobs, actor, actorName, reason));
    }

    public Task<NCPersonnelChangeData> ChangeNCCharacterPositionAsync(NCCharacterId target, string newJobId,
        int positionLimit, IReadOnlyCollection<string> organizationJobs, NCEmploymentEventType eventType,
        NCCharacterId actor, string actorName, string reason)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ChangeNCCharacterPositionAsync(target, newJobId, positionLimit,
            organizationJobs, eventType, actor, actorName, reason));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCPersonnelEmployeeData>> GetNCPersonnelRosterAsync(
        IReadOnlyCollection<string> jobIds)
    {
        if (jobIds.Count == 0)
            return Array.Empty<NCPersonnelEmployeeData>();
        var jobs = jobIds.Distinct().ToArray();
        await using var db = await GetDb();
        return await db.DbContext.NCCharacterEmployment.AsNoTracking()
            .Where(value => value.State == NCEmploymentState.Active && jobs.Contains(value.JobPrototypeId))
            .OrderBy(value => value.JobPrototypeId).ThenBy(value => value.Profile.CharacterName)
            .Select(value => new NCPersonnelEmployeeData(new NCCharacterId(value.ProfileId),
                value.Profile.CharacterName, value.JobPrototypeId, value.StartedAt))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCPersonnelCandidateData>> SearchNCPersonnelCandidatesAsync(string query, int limit)
    {
        query = query.Trim();
        await using var db = await GetDb();
        var profiles = db.DbContext.Profile.AsNoTracking()
            .Where(value => value.NCEmployment == null || value.NCEmployment.State != NCEmploymentState.Active);
        if (query.Length > 0)
            profiles = profiles.Where(value => EF.Functions.Like(value.CharacterName, $"%{query}%"));
        return await profiles.OrderBy(value => value.CharacterName).Take(Math.Clamp(limit, 1, 200))
            .Select(value => new NCPersonnelCandidateData(new NCCharacterId(value.Id), value.CharacterName))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCPersonnelHistoryData>> GetNCPersonnelHistoryAsync(
        NCCharacterId characterId, int limit)
    {
        if (!characterId.IsValid)
            return Array.Empty<NCPersonnelHistoryData>();
        await using var db = await GetDb();
        return await db.DbContext.NCEmploymentEvents.AsNoTracking()
            .Where(value => value.ProfileId == characterId.Value)
            .OrderByDescending(value => value.CreatedAt).Take(Math.Clamp(limit, 1, 200))
            .Select(value => new NCPersonnelHistoryData(value.PreviousJobPrototypeId,
                value.NewJobPrototypeId, value.EventType, value.Reason, value.ActorName, value.CreatedAt))
            .ToListAsync();
    }

    public async Task<string?> GetNCActiveJobAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;
        await using var db = await GetDb();
        return await db.DbContext.NCCharacterEmployment.AsNoTracking()
            .Where(value => value.ProfileId == characterId.Value && value.State == NCEmploymentState.Active)
            .Select(value => value.JobPrototypeId).SingleOrDefaultAsync();
    }

    public async Task<NCPersonnelChangeData> HireNCCharacterAsync(NCCharacterId target, string jobId,
        int positionLimit, NCCharacterId actor, string actorName, string reason)
    {
        actorName = actorName.Trim();
        reason = reason.Trim();
        if (!target.IsValid || !actor.IsValid || target == actor || positionLimit < 1 || jobId.Length is < 1 or > 64 ||
            actorName.Length is < 1 or > 128 || reason.Length is < 1 or > 512)
            return new(NCPersonnelChangeResult.InvalidEmployment, null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var profile = await db.DbContext.Profile.Include(value => value.Preference).Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == target.Value);
        if (profile == null)
            return new(NCPersonnelChangeResult.CharacterNotFound, null);
        if (profile.NCEmployment is { State: NCEmploymentState.Active })
            return new(NCPersonnelChangeResult.CharacterUnavailable, new NetUserId(profile.Preference.UserId));

        var occupied = await db.DbContext.NCCharacterEmployment
            .CountAsync(value => value.State == NCEmploymentState.Active && value.JobPrototypeId == jobId);
        if (occupied >= positionLimit)
            return new(NCPersonnelChangeResult.PositionFull, new NetUserId(profile.Preference.UserId));

        var now = DateTime.UtcNow;
        var previousJob = profile.NCEmployment?.JobPrototypeId;
        var previousState = profile.NCEmployment?.State;
        var employment = profile.NCEmployment ?? new NCCharacterEmployment { ProfileId = profile.Id };
        if (profile.NCEmployment == null)
            profile.NCEmployment = employment;
        employment.JobPrototypeId = jobId;
        employment.State = NCEmploymentState.Active;
        employment.StartedAt = now;
        employment.UpdatedAt = now;
        employment.Events.Add(new NCEmploymentEvent
        {
            EventType = NCEmploymentEventType.Hired, PreviousJobPrototypeId = previousJob,
            NewJobPrototypeId = jobId, PreviousState = previousState, NewState = NCEmploymentState.Active,
            Reason = reason, ActorProfileId = actor.Value, ActorName = actorName, CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(NCPersonnelChangeResult.Success, new NetUserId(profile.Preference.UserId));
    }

    public async Task<NCPersonnelChangeData> TerminateNCCharacterAsync(NCCharacterId target,
        IReadOnlyCollection<string> organizationJobs, NCCharacterId actor, string actorName, string reason)
    {
        actorName = actorName.Trim();
        reason = reason.Trim();
        if (!target.IsValid || !actor.IsValid || target == actor || organizationJobs.Count == 0 ||
            actorName.Length is < 1 or > 128 || reason.Length is < 1 or > 512)
            return new(NCPersonnelChangeResult.InvalidEmployment, null);
        var jobs = organizationJobs.Distinct().ToArray();
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var profile = await db.DbContext.Profile.Include(value => value.Preference).Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == target.Value);
        if (profile?.NCEmployment == null)
            return new(NCPersonnelChangeResult.CharacterNotFound, null);
        var employment = profile.NCEmployment;
        if (employment.State != NCEmploymentState.Active || !jobs.Contains(employment.JobPrototypeId))
            return new(NCPersonnelChangeResult.InvalidEmployment, new NetUserId(profile.Preference.UserId));
        var now = DateTime.UtcNow;
        var previousJob = employment.JobPrototypeId;
        employment.State = NCEmploymentState.Terminated;
        employment.UpdatedAt = now;
        employment.Events.Add(new NCEmploymentEvent
        {
            EventType = NCEmploymentEventType.Terminated, PreviousJobPrototypeId = previousJob,
            NewJobPrototypeId = null, PreviousState = NCEmploymentState.Active,
            NewState = NCEmploymentState.Terminated, Reason = reason, ActorProfileId = actor.Value,
            ActorName = actorName, CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(NCPersonnelChangeResult.Success, new NetUserId(profile.Preference.UserId));
    }

    public async Task<NCPersonnelChangeData> ChangeNCCharacterPositionAsync(NCCharacterId target,
        string newJobId, int positionLimit, IReadOnlyCollection<string> organizationJobs,
        NCEmploymentEventType eventType, NCCharacterId actor, string actorName, string reason)
    {
        actorName = actorName.Trim();
        reason = reason.Trim();
        if (!target.IsValid || !actor.IsValid || target == actor || positionLimit < 1 ||
            newJobId.Length is < 1 or > 64 || organizationJobs.Count == 0 ||
            eventType is not (NCEmploymentEventType.Promoted or NCEmploymentEventType.Demoted or
                NCEmploymentEventType.Transferred) || actorName.Length is < 1 or > 128 ||
            reason.Length is < 1 or > 512)
            return new(NCPersonnelChangeResult.InvalidEmployment, null);

        var jobs = organizationJobs.Distinct().ToArray();
        if (!jobs.Contains(newJobId))
            return new(NCPersonnelChangeResult.InvalidEmployment, null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var profile = await db.DbContext.Profile.Include(value => value.Preference).Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == target.Value);
        if (profile?.NCEmployment == null)
            return new(NCPersonnelChangeResult.CharacterNotFound, null);

        var employment = profile.NCEmployment;
        if (employment.State != NCEmploymentState.Active || !jobs.Contains(employment.JobPrototypeId) ||
            employment.JobPrototypeId == newJobId)
            return new(NCPersonnelChangeResult.InvalidEmployment, new NetUserId(profile.Preference.UserId));

        var occupied = await db.DbContext.NCCharacterEmployment
            .CountAsync(value => value.State == NCEmploymentState.Active && value.JobPrototypeId == newJobId);
        if (occupied >= positionLimit)
            return new(NCPersonnelChangeResult.PositionFull, new NetUserId(profile.Preference.UserId));

        var now = DateTime.UtcNow;
        var previousJob = employment.JobPrototypeId;
        employment.JobPrototypeId = newJobId;
        employment.StartedAt = now;
        employment.UpdatedAt = now;
        employment.Events.Add(new NCEmploymentEvent
        {
            EventType = eventType, PreviousJobPrototypeId = previousJob, NewJobPrototypeId = newJobId,
            PreviousState = NCEmploymentState.Active, NewState = NCEmploymentState.Active,
            Reason = reason, ActorProfileId = actor.Value, ActorName = actorName, CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(NCPersonnelChangeResult.Success, new NetUserId(profile.Preference.UserId));
    }
}
