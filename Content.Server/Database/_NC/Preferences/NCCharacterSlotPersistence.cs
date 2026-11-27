// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Microsoft.EntityFrameworkCore;
using Robust.Shared.Network;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

/// <summary>Stable database identity of one character slot.</summary>
public sealed record NCCharacterSlotData(int ProfileId, int Slot);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCCharacterSlotData>> GetNCCharacterSlotsAsync(NetUserId userId);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCCharacterSlotData>> GetNCCharacterSlotsAsync(NetUserId userId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCCharacterSlotsAsync(userId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCCharacterSlotData>> GetNCCharacterSlotsAsync(NetUserId userId)
    {
        await using var db = await GetDb();
        return await db.DbContext.Profile
            .Where(profile => profile.Preference.UserId == userId.UserId)
            .Select(profile => new NCCharacterSlotData(profile.Id, profile.Slot))
            .ToListAsync();
    }
}
