// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetNCRoundSnapshotSectionsAsync(
        Guid snapshotId, string sectionType, string ownerType, string ownerId);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetNCRoundSnapshotSectionsAsync(
        Guid snapshotId, string sectionType, string ownerType, string ownerId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null,
            () => _db.GetNCRoundSnapshotSectionsAsync(snapshotId, sectionType, ownerType, ownerId));
    }
}

public abstract partial class ServerDbBase
{
    /// <summary>All participating domains use one chosen committed manifest throughout recovery.</summary>
    public async Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetNCRoundSnapshotSectionsAsync(
        Guid snapshotId, string sectionType, string ownerType, string ownerId)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("Snapshot identity is required.", nameof(snapshotId));
        ValidateSectionIdentity(sectionType, ownerType, ownerId);
        await using var db = await GetDb();
        if (!await db.DbContext.NCRoundSnapshots.AsNoTracking().AnyAsync(value =>
                value.SnapshotId == snapshotId && value.Status == NCRoundSnapshotStatus.Committed))
            return [];
        return await db.DbContext.NCRoundSnapshotSections.AsNoTracking()
            .Where(value => value.SnapshotId == snapshotId && value.SectionType == sectionType &&
                            value.OwnerType == ownerType && value.OwnerId == ownerId)
            .OrderBy(value => value.ChunkIndex)
            .Select(value => new NCRoundSnapshotSectionData(value.SnapshotId, value.SectionType, value.OwnerType,
                value.OwnerId, value.ChunkIndex, value.FormatVersion, value.EntityCount, value.Payload, value.Checksum))
            .ToListAsync();
    }
}
