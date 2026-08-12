// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>
/// Detached database representation. Payload has already passed DataDefinition and semantic validation
/// in NC medicine; this DTO contains no runtime entity or network identity.
/// </summary>
public sealed record NCMedicalStoredSnapshotData(
    NCCharacterId CharacterId,
    int SnapshotVersion,
    long Revision,
    string Payload,
    DateTime CapturedUtc,
    DateTime UpdatedUtc);

public enum NCMedicalSnapshotWriteStatus : byte
{
    Saved,
    Conflict,
    CharacterNotFound,
    Invalid,
}

public sealed record NCMedicalSnapshotWriteResult(
    NCMedicalSnapshotWriteStatus Status,
    long Revision);

public partial interface IServerDbManager
{
    Task<NCMedicalStoredSnapshotData?> GetNCMedicalSnapshotAsync(NCCharacterId characterId);

    Task<NCMedicalSnapshotWriteResult> TrySaveNCMedicalSnapshotAsync(
        NCMedicalStoredSnapshotData snapshot,
        long expectedRevision);
}

public sealed partial class ServerDbManager
{
    public Task<NCMedicalStoredSnapshotData?> GetNCMedicalSnapshotAsync(NCCharacterId characterId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCMedicalSnapshotAsync(characterId));
    }

    public Task<NCMedicalSnapshotWriteResult> TrySaveNCMedicalSnapshotAsync(
        NCMedicalStoredSnapshotData snapshot,
        long expectedRevision)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TrySaveNCMedicalSnapshotAsync(snapshot, expectedRevision));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCMedicalStoredSnapshotData?> GetNCMedicalSnapshotAsync(NCCharacterId characterId)
    {
        if (!characterId.IsValid)
            return null;

        await using var db = await GetDb();
        var row = await db.DbContext.NCMedicalCharacterSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        return row == null ? null : ToData(row);
    }

    public async Task<NCMedicalSnapshotWriteResult> TrySaveNCMedicalSnapshotAsync(
        NCMedicalStoredSnapshotData snapshot,
        long expectedRevision)
    {
        if (!snapshot.CharacterId.IsValid ||
            snapshot.SnapshotVersion <= 0 ||
            snapshot.Revision < 0 ||
            expectedRevision < 0 ||
            snapshot.Revision != expectedRevision ||
            snapshot.Payload.Length is <= 0 or > NCMedicalCharacterSnapshot.MaximumPayloadLength ||
            snapshot.CapturedUtc.Kind != DateTimeKind.Utc)
        {
            return new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.Invalid, expectedRevision);
        }

        await using var db = await GetDb();
        if (expectedRevision == 0)
        {
            if (!await db.DbContext.Profile.AnyAsync(value => value.Id == snapshot.CharacterId.Value))
            {
                return new NCMedicalSnapshotWriteResult(
                    NCMedicalSnapshotWriteStatus.CharacterNotFound,
                    0);
            }

            var existingRevision = await db.DbContext.NCMedicalCharacterSnapshots
                .AsNoTracking()
                .Where(value => value.ProfileId == snapshot.CharacterId.Value)
                .Select(value => (long?) value.Revision)
                .SingleOrDefaultAsync();
            if (existingRevision is { } conflictRevision)
            {
                return new NCMedicalSnapshotWriteResult(
                    NCMedicalSnapshotWriteStatus.Conflict,
                    conflictRevision);
            }

            var now = DateTime.UtcNow;
            var row = new NCMedicalCharacterSnapshot
            {
                ProfileId = snapshot.CharacterId.Value,
                SnapshotVersion = snapshot.SnapshotVersion,
                Revision = 1,
                Payload = snapshot.Payload,
                CapturedAt = snapshot.CapturedUtc,
                UpdatedAt = now,
            };
            db.DbContext.NCMedicalCharacterSnapshots.Add(row);
            try
            {
                await db.DbContext.SaveChangesAsync();
                return new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.Saved, 1);
            }
            catch (DbUpdateException)
            {
                var concurrentRevision = await db.DbContext.NCMedicalCharacterSnapshots
                    .AsNoTracking()
                    .Where(value => value.ProfileId == snapshot.CharacterId.Value)
                    .Select(value => (long?) value.Revision)
                    .SingleOrDefaultAsync();
                return concurrentRevision is { } revision
                    ? new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.Conflict, revision)
                    : new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.CharacterNotFound, 0);
            }
        }

        var capturedUtc = snapshot.CapturedUtc;
        var updatedUtc = DateTime.UtcNow;
        var updated = await db.DbContext.NCMedicalCharacterSnapshots
            .Where(value => value.ProfileId == snapshot.CharacterId.Value &&
                            value.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.SnapshotVersion, snapshot.SnapshotVersion)
                .SetProperty(value => value.Payload, snapshot.Payload)
                .SetProperty(value => value.CapturedAt, capturedUtc)
                .SetProperty(value => value.UpdatedAt, updatedUtc)
                .SetProperty(value => value.Revision, value => value.Revision + 1));
        if (updated == 1)
            return new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.Saved, expectedRevision + 1);

        var current = await db.DbContext.NCMedicalCharacterSnapshots
            .AsNoTracking()
            .Where(value => value.ProfileId == snapshot.CharacterId.Value)
            .Select(value => (long?) value.Revision)
            .SingleOrDefaultAsync();
        if (current is { } currentRevision)
            return new NCMedicalSnapshotWriteResult(NCMedicalSnapshotWriteStatus.Conflict, currentRevision);

        var profileExists = await db.DbContext.Profile
            .AsNoTracking()
            .AnyAsync(value => value.Id == snapshot.CharacterId.Value);
        return new NCMedicalSnapshotWriteResult(
            profileExists ? NCMedicalSnapshotWriteStatus.Conflict : NCMedicalSnapshotWriteStatus.CharacterNotFound,
            0);
    }

    private static NCMedicalStoredSnapshotData ToData(NCMedicalCharacterSnapshot row)
    {
        return new NCMedicalStoredSnapshotData(
            new NCCharacterId(row.ProfileId),
            row.SnapshotVersion,
            row.Revision,
            row.Payload,
            DateTime.SpecifyKind(row.CapturedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));
    }
}
