// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Content.Shared.Database._NC.Police;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Content.Server.Database;

public sealed record NCPoliceRecordData(
    NCCharacterId CharacterId,
    string CharacterName,
    string? JobPrototypeId,
    NCPoliceOperationalStatus Status,
    string? Reason,
    int? UpdatedByProfileId,
    string UpdatedByName,
    DateTime? UpdatedAt);

public sealed record NCPoliceRecordEventData(
    long Id,
    NCCharacterId CharacterId,
    NCPoliceRecordEventType EventType,
    NCPoliceOperationalStatus PreviousStatus,
    NCPoliceOperationalStatus NewStatus,
    string? Reason,
    int? ActorProfileId,
    string ActorName,
    DateTime CreatedAt);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPoliceRecordData>> SearchNCPoliceRecordsAsync(string query, int limit);
    Task<NCPoliceRecordData?> GetNCPoliceRecordAsync(NCCharacterId characterId);
    Task<IReadOnlyList<NCPoliceRecordEventData>> GetNCPoliceRecordHistoryAsync(NCCharacterId characterId, int limit);
    Task<NCPoliceRecordData?> SetNCPoliceStatusAsync(
        NCCharacterId characterId,
        NCPoliceOperationalStatus status,
        string? reason,
        NCCharacterId? actorCharacterId,
        string actorName,
        NCPoliceRecordEventType eventType = NCPoliceRecordEventType.StatusChanged);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPoliceRecordData>> SearchNCPoliceRecordsAsync(string query, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.SearchNCPoliceRecordsAsync(query, limit));
    }

    public Task<NCPoliceRecordData?> GetNCPoliceRecordAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceRecordAsync(characterId));
    }

    public Task<IReadOnlyList<NCPoliceRecordEventData>> GetNCPoliceRecordHistoryAsync(NCCharacterId characterId, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceRecordHistoryAsync(characterId, limit));
    }

    public Task<NCPoliceRecordData?> SetNCPoliceStatusAsync(
        NCCharacterId characterId,
        NCPoliceOperationalStatus status,
        string? reason,
        NCCharacterId? actorCharacterId,
        string actorName,
        NCPoliceRecordEventType eventType = NCPoliceRecordEventType.StatusChanged)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPoliceStatusAsync(
            characterId,
            status,
            reason,
            actorCharacterId,
            actorName,
            eventType));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCPoliceRecordData>> SearchNCPoliceRecordsAsync(string query, int limit)
    {
        var normalized = query.Trim().ToLowerInvariant();
        await using var db = await GetDb();
        IQueryable<Profile> profileQuery = db.DbContext.Profile
            .AsNoTracking()
            .Include(profile => profile.NCEmployment);

        // An empty query represents the city registry shown when the terminal opens.
        if (normalized.Length > 0)
            profileQuery = profileQuery.Where(profile => profile.CharacterName.ToLower().Contains(normalized));

        var profiles = await profileQuery
            .OrderBy(profile => profile.CharacterName)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();

        var profileIds = profiles.Select(profile => profile.Id).ToList();
        var records = await db.DbContext.NCPoliceRecords
            .AsNoTracking()
            .Where(record => profileIds.Contains(record.ProfileId))
            .ToDictionaryAsync(record => record.ProfileId);

        return profiles
            .Select(profile => ToData(profile, records.GetValueOrDefault(profile.Id)))
            .ToList();
    }

    public async Task<NCPoliceRecordData?> GetNCPoliceRecordAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var profile = await db.DbContext.Profile
            .AsNoTracking()
            .Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == characterId.Value);
        if (profile == null)
            return null;

        var record = await db.DbContext.NCPoliceRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        return ToData(profile, record);
    }

    public async Task<IReadOnlyList<NCPoliceRecordEventData>> GetNCPoliceRecordHistoryAsync(
        NCCharacterId characterId,
        int limit)
    {
        if (!characterId.IsValid)
            return Array.Empty<NCPoliceRecordEventData>();

        await using var db = await GetDb();
        return await db.DbContext.NCPoliceRecordEvents
            .AsNoTracking()
            .Where(value => value.ProfileId == characterId.Value)
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(value => new NCPoliceRecordEventData(
                value.Id,
                new NCCharacterId(value.ProfileId),
                value.EventType,
                value.PreviousStatus,
                value.NewStatus,
                value.Reason,
                value.ActorProfileId,
                value.ActorName,
                value.CreatedAt))
            .ToListAsync();
    }

    public async Task<NCPoliceRecordData?> SetNCPoliceStatusAsync(
        NCCharacterId characterId,
        NCPoliceOperationalStatus status,
        string? reason,
        NCCharacterId? actorCharacterId,
        string actorName,
        NCPoliceRecordEventType eventType = NCPoliceRecordEventType.StatusChanged)
    {
        if (!characterId.IsValid)
            return null;

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var normalizedActor = actorName.Trim();
        if (normalizedReason?.Length > 256 || normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var profile = await db.DbContext.Profile
            .Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == characterId.Value);
        if (profile == null)
            return null;

        var record = await db.DbContext.NCPoliceRecords
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        var previousStatus = record?.Status ?? NCPoliceOperationalStatus.None;
        if (record != null && record.Status == status && record.Reason == normalizedReason)
            return ToData(profile, record);

        var now = DateTime.UtcNow;
        if (record == null)
        {
            record = new NCPoliceRecord { ProfileId = characterId.Value };
            db.DbContext.NCPoliceRecords.Add(record);
        }

        record.Status = status;
        record.Reason = normalizedReason;
        record.UpdatedByProfileId = actorCharacterId is { IsValid: true } actor ? actor.Value : null;
        record.UpdatedByName = normalizedActor;
        record.UpdatedAt = now;
        record.Events.Add(new NCPoliceRecordEvent
        {
            ProfileId = characterId.Value,
            EventType = eventType,
            PreviousStatus = previousStatus,
            NewStatus = status,
            Reason = normalizedReason,
            ActorProfileId = record.UpdatedByProfileId,
            ActorName = normalizedActor,
            CreatedAt = now,
        });

        await db.DbContext.SaveChangesAsync();
        return ToData(profile, record);
    }

    private static NCPoliceRecordData ToData(Profile profile, NCPoliceRecord? record)
    {
        return new NCPoliceRecordData(
            new NCCharacterId(profile.Id),
            profile.CharacterName,
            profile.NCEmployment?.JobPrototypeId,
            record?.Status ?? NCPoliceOperationalStatus.None,
            record?.Reason,
            record?.UpdatedByProfileId,
            record?.UpdatedByName ?? string.Empty,
            record?.UpdatedAt);
    }
}
