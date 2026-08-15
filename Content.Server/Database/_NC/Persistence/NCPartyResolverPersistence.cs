// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public sealed record NCCharacterPartyProjection(NCCharacterId CharacterId, string DisplayName);

public partial interface IServerDbManager
{
    Task<NCCharacterPartyProjection?> GetNCCharacterPartyProjectionAsync(NCCharacterId characterId);
}

public sealed partial class ServerDbManager
{
    public Task<NCCharacterPartyProjection?> GetNCCharacterPartyProjectionAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCCharacterPartyProjectionAsync(characterId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCCharacterPartyProjection?> GetNCCharacterPartyProjectionAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        return await db.DbContext.Profile
            .AsNoTracking()
            .Where(profile => profile.Id == characterId.Value)
            .Select(profile => new NCCharacterPartyProjection(characterId, profile.CharacterName))
            .SingleOrDefaultAsync();
    }
}
