// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCMissionRunData(
    NCMissionRunId RunId,
    NCMissionSlotId SlotId,
    string DefinitionId,
    string TemplateId,
    int TemplateRevision,
    NCZoneDeploymentId DeploymentId,
    NCMissionRunState State,
    IReadOnlySet<NCCharacterId> Participants,
    int Seed,
    DateTime StartedAt,
    DateTime? CompletedAt,
    NCMissionRunResult Result,
    string? Error,
    int Version);

public partial interface IServerDbManager
{
    Task<NCMissionRunData?> TryCreateNCMissionRunAsync(NCMissionRunData run);
    Task<NCMissionRunData?> UpdateNCMissionRunAsync(
        NCMissionRunId runId,
        NCMissionRunState state,
        NCMissionRunResult result,
        DateTime? completedAt,
        string? error,
        int expectedVersion);
    Task<IReadOnlyList<NCMissionRunData>> GetRecoverableNCMissionRunsAsync();
}

public sealed partial class ServerDbManager
{
    public Task<NCMissionRunData?> TryCreateNCMissionRunAsync(NCMissionRunData run)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryCreateNCMissionRunAsync(run));
    }

    public Task<NCMissionRunData?> UpdateNCMissionRunAsync(
        NCMissionRunId runId,
        NCMissionRunState state,
        NCMissionRunResult result,
        DateTime? completedAt,
        string? error,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpdateNCMissionRunAsync(
            runId, state, result, completedAt, error, expectedVersion));
    }

    public Task<IReadOnlyList<NCMissionRunData>> GetRecoverableNCMissionRunsAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetRecoverableNCMissionRunsAsync());
    }
}

public abstract partial class ServerDbBase
{
    // Legacy EF type/table names remain unchanged so the gameplay rename to Missions requires no destructive
    // database migration and existing run reservations keep their foreign-key relationships.
    public async Task<NCMissionRunData?> TryCreateNCMissionRunAsync(NCMissionRunData run)
    {
        if (!run.RunId.IsValid || !run.SlotId.IsValid || !run.DeploymentId.IsValid ||
            run.Participants.Count == 0 || run.Participants.Any(value => !value.IsValid))
            return null;

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var terminal = new[] { (int) NCMissionRunState.Closed, (int) NCMissionRunState.Failed };
        if (await db.DbContext.NCDungeonRuns.AnyAsync(value =>
                value.DungeonSlotId == run.SlotId.ZoneId.Value && !terminal.Contains(value.State)))
            return null;
        if (await db.DbContext.NCDungeonRuns.AnyAsync(value => value.RunId == run.RunId.Value ||
                value.DeploymentId == run.DeploymentId.Value))
            return null;
        var profileIds = run.Participants.Select(value => value.Value).ToArray();
        if (await db.DbContext.Profile.CountAsync(value => profileIds.Contains(value.Id)) != profileIds.Length)
            return null;

        var record = new NCDungeonRunRecord
        {
            RunId = run.RunId.Value,
            DungeonSlotId = run.SlotId.ZoneId.Value,
            DefinitionId = run.DefinitionId,
            TemplateId = run.TemplateId,
            TemplateRevision = run.TemplateRevision,
            DeploymentId = run.DeploymentId.Value,
            State = (int) run.State,
            Seed = run.Seed,
            StartedAt = run.StartedAt,
            Result = (int) run.Result,
            Version = 1,
        };
        foreach (var participant in run.Participants)
            record.Participants.Add(new NCDungeonRunParticipant { RunId = run.RunId.Value, ProfileId = participant.Value });
        db.DbContext.NCDungeonRuns.Add(record);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return ToData(record);
    }

    public async Task<NCMissionRunData?> UpdateNCMissionRunAsync(
        NCMissionRunId runId,
        NCMissionRunState state,
        NCMissionRunResult result,
        DateTime? completedAt,
        string? error,
        int expectedVersion)
    {
        if (!runId.IsValid || expectedVersion <= 0 || error?.Length > 1024)
            return null;
        await using var db = await GetDb();
        var record = await db.DbContext.NCDungeonRuns
            .Include(value => value.Participants)
            .SingleOrDefaultAsync(value => value.RunId == runId.Value && value.Version == expectedVersion);
        if (record == null)
            return null;
        record.State = (int) state;
        record.Result = (int) result;
        record.CompletedAt = completedAt;
        record.Error = error;
        record.Version++;
        await db.DbContext.SaveChangesAsync();
        return ToData(record);
    }

    public async Task<IReadOnlyList<NCMissionRunData>> GetRecoverableNCMissionRunsAsync()
    {
        await using var db = await GetDb();
        var records = await db.DbContext.NCDungeonRuns.AsNoTracking()
            .Include(value => value.Participants)
            .Where(value => value.State != (int) NCMissionRunState.Closed &&
                value.State != (int) NCMissionRunState.Failed)
            .ToListAsync();
        return records.Select(ToData).ToArray();
    }

    private static NCMissionRunData ToData(NCDungeonRunRecord record)
    {
        return new NCMissionRunData(
            new NCMissionRunId(record.RunId),
            new NCMissionSlotId(new NCZoneId(record.DungeonSlotId)),
            record.DefinitionId,
            record.TemplateId,
            record.TemplateRevision,
            new NCZoneDeploymentId(record.DeploymentId),
            (NCMissionRunState) record.State,
            record.Participants.Select(value => new NCCharacterId(value.ProfileId)).ToHashSet(),
            record.Seed,
            record.StartedAt,
            record.CompletedAt,
            (NCMissionRunResult) record.Result,
            record.Error,
            record.Version);
    }
}
