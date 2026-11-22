// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCSkillProgressData(long ActiveTicks, string[] Learned, int SurvivedRounds, int? LastSurvivedRound);

public partial interface IServerDbManager
{
    Task<NCSkillProgressData> GetNCSkillProgressAsync(NCCharacterId id);
    Task<bool> SaveNCSkillProgressAsync(NCCharacterId id, NCSkillProgressData progress);
}

public sealed partial class ServerDbManager
{
    public Task<NCSkillProgressData> GetNCSkillProgressAsync(NCCharacterId id)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCSkillProgressAsync(id));
    }

    public Task<bool> SaveNCSkillProgressAsync(NCCharacterId id, NCSkillProgressData progress)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SaveNCSkillProgressAsync(id, progress));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCSkillProgressData> GetNCSkillProgressAsync(NCCharacterId id)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCCharacterSkillProgress.AsNoTracking()
            .SingleOrDefaultAsync(progress => progress.ProfileId == id.Value);
        return row == null
            ? new NCSkillProgressData(0, [], 0, null)
            : new NCSkillProgressData(row.ActiveTicks, JsonSerializer.Deserialize<string[]>(row.LearnedSkills) ?? [],
                row.SurvivedRounds, row.LastSurvivedRound);
    }

    public async Task<bool> SaveNCSkillProgressAsync(NCCharacterId id, NCSkillProgressData progress)
    {
        await using var db = await GetDb();
        var row = await db.DbContext.NCCharacterSkillProgress.FindAsync(id.Value);
        if (row == null)
        {
            if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == id.Value))
                return false;

            row = new NCCharacterSkillProgress { ProfileId = id.Value };
            db.DbContext.NCCharacterSkillProgress.Add(row);
        }

        row.ActiveTicks = progress.ActiveTicks;
        row.LearnedSkills = JsonSerializer.Serialize(progress.Learned.Order());
        row.SurvivedRounds = progress.SurvivedRounds;
        row.LastSurvivedRound = progress.LastSurvivedRound;
        await db.DbContext.SaveChangesAsync();
        return true;
    }
}
