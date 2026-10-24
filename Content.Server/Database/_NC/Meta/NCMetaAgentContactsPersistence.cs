// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCAgentDirectoryData>> GetNCMetaAgentRecipientsAsync(NCCharacterId owner, string query, int limit);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCAgentDirectoryData>> GetNCMetaAgentRecipientsAsync(NCCharacterId owner, string query, int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCMetaAgentRecipientsAsync(owner, query, limit));
    }
}

public abstract partial class ServerDbBase
{
    /// <summary>Existing Agent contact/directory visibility with a bounded projection and no inbox/feed loading.</summary>
    public async Task<IReadOnlyList<NCAgentDirectoryData>> GetNCMetaAgentRecipientsAsync(NCCharacterId owner, string query, int limit)
    {
        if (!owner.IsValid || query is not { Length: <= 64 } || limit is < 1 or > 16) return [];
        await using var db = await GetDb();
        if (!await db.DbContext.NCAgentProfiles.AsNoTracking().AnyAsync(value => value.ProfileId == owner.Value)) return [];
        var lower = query.Trim().ToLowerInvariant();
        var upper = query.Trim().ToUpperInvariant();
        return await db.DbContext.NCAgentProfiles.AsNoTracking()
            .Where(value => value.ProfileId != owner.Value &&
                !db.DbContext.NCAgentBlocks.Any(block =>
                    block.OwnerProfileId == owner.Value && block.BlockedProfileId == value.ProfileId ||
                    block.OwnerProfileId == value.ProfileId && block.BlockedProfileId == owner.Value) &&
                (lower.Length == 0
                    ? db.DbContext.NCAgentContacts.Any(contact => contact.OwnerProfileId == owner.Value && contact.ContactProfileId == value.ProfileId)
                    : value.Address.ToLower().Contains(lower) || (value.NormalizedAlias ?? value.Profile.CharacterName.ToUpper()).Contains(upper)))
            .OrderByDescending(value => db.DbContext.NCAgentContacts.Any(contact => contact.OwnerProfileId == owner.Value && contact.ContactProfileId == value.ProfileId))
            .ThenBy(value => value.Alias ?? value.Profile.CharacterName).ThenBy(value => value.ProfileId)
            .Take(limit).Select(value => new NCAgentDirectoryData(new NCCharacterId(value.ProfileId),
                value.Alias ?? value.Profile.CharacterName, value.Address,
                db.DbContext.NCAgentContacts.Any(contact => contact.OwnerProfileId == owner.Value && contact.ContactProfileId == value.ProfileId),
                (NCAgentPresenceStatus) value.Presence)).ToListAsync();
    }
}
