// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Preferences;
using Content.Shared.Roles;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCCharacterEmploymentData>> GetNCCharacterEmploymentDataAsync(NetUserId userId);
    Task<bool> SetNCCharacterEmploymentAsync(NetUserId userId, int profileId, ProtoId<JobPrototype>? job);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCCharacterEmploymentData>> GetNCCharacterEmploymentDataAsync(NetUserId userId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCCharacterEmploymentDataAsync(userId));
    }

    public Task<bool> SetNCCharacterEmploymentAsync(
        NetUserId userId,
        int profileId,
        ProtoId<JobPrototype>? job)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCCharacterEmploymentAsync(userId, profileId, job));
    }
}

public abstract partial class ServerDbBase
{
    /// <summary>
    /// Loads character IDs and current jobs together so runtime code never treats the account ID as character state.
    /// </summary>
    public async Task<IReadOnlyList<NCCharacterEmploymentData>> GetNCCharacterEmploymentDataAsync(NetUserId userId)
    {
        await using var db = await GetDb();

        return await db.DbContext.Profile
            .Where(profile => profile.Preference.UserId == userId.UserId)
            .Select(profile => new NCCharacterEmploymentData(
                profile.Id,
                profile.Slot,
                profile.NCEmployment == null ? null : profile.NCEmployment.JobPrototypeId,
                profile.NCEmployment == null ? null : profile.NCEmployment.State))
            .ToListAsync();
    }

    /// <summary>
    /// Assigns, transfers, or terminates the selected character's concrete job.
    /// </summary>
    public async Task<bool> SetNCCharacterEmploymentAsync(
        NetUserId userId,
        int profileId,
        ProtoId<JobPrototype>? job)
    {
        await using var db = await GetDb();
        var profile = await db.DbContext.Profile
            .Include(value => value.Preference)
            .Include(value => value.NCEmployment)
            .SingleOrDefaultAsync(value => value.Id == profileId && value.Preference.UserId == userId.UserId);

        if (profile == null)
            return false;

        var now = DateTime.UtcNow;
        var previousJob = profile.NCEmployment?.JobPrototypeId;
        var previousState = profile.NCEmployment?.State;
        if (job == null)
        {
            if (profile.NCEmployment == null)
                return true;

            profile.NCEmployment.State = NCEmploymentState.Terminated;
            profile.NCEmployment.UpdatedAt = now;
        }
        else if (profile.NCEmployment == null)
        {
            profile.NCEmployment = new NCCharacterEmployment
            {
                ProfileId = profile.Id,
                JobPrototypeId = job.Value.Id,
                State = NCEmploymentState.Active,
                StartedAt = now,
                UpdatedAt = now,
            };
        }
        else
        {
            var employment = profile.NCEmployment;
            if (employment.State != NCEmploymentState.Active || employment.JobPrototypeId != job.Value.Id)
                employment.StartedAt = now;

            employment.JobPrototypeId = job.Value.Id;
            employment.State = NCEmploymentState.Active;
            employment.UpdatedAt = now;
        }

        if (profile.NCEmployment != null &&
            (previousJob != profile.NCEmployment.JobPrototypeId || previousState != profile.NCEmployment.State))
        {
            profile.NCEmployment.Events.Add(new NCEmploymentEvent
            {
                EventType = previousState == null
                    ? NCEmploymentEventType.EntrySelected
                    : NCEmploymentEventType.AdministrativeChange,
                PreviousJobPrototypeId = previousJob,
                NewJobPrototypeId = profile.NCEmployment.State == NCEmploymentState.Active
                    ? profile.NCEmployment.JobPrototypeId
                    : null,
                PreviousState = previousState,
                NewState = profile.NCEmployment.State,
                Reason = previousState == null
                    ? "Initial department selection"
                    : "Administrative employment change",
                ActorProfileId = profile.Id,
                ActorName = profile.CharacterName,
                CreatedAt = now,
            });
        }

        await db.DbContext.SaveChangesAsync();
        return true;
    }
}
