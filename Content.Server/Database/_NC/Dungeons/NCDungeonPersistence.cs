// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Dungeons;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCDungeonRunData(
    NCDungeonRunId RunId,
    NCDungeonSlotId SlotId,
    string DefinitionId,
    string TemplateId,
    int TemplateRevision,
    NCZoneDeploymentId DeploymentId,
    NCDungeonRunState State,
    IReadOnlySet<NCCharacterId> Participants,
    int Seed,
    DateTime StartedAt,
    DateTime? CompletedAt,
    NCDungeonRunResult Result,
    string? Error,
    int Version);

public partial interface IServerDbManager
{
    Task<NCDungeonRunData?> TryCreateNCDungeonRunAsync(NCDungeonRunData run);
    Task<NCDungeonRunData?> UpdateNCDungeonRunAsync(
        NCDungeonRunId runId,
        NCDungeonRunState state,
        NCDungeonRunResult result,
        DateTime? completedAt,
        string? error,
        int expectedVersion);
    Task<IReadOnlyList<NCDungeonRunData>> GetRecoverableNCDungeonRunsAsync();
}

public sealed partial class ServerDbManager
{
    public Task<NCDungeonRunData?> TryCreateNCDungeonRunAsync(NCDungeonRunData run)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryCreateNCDungeonRunAsync(run));
    }

    public Task<NCDungeonRunData?> UpdateNCDungeonRunAsync(
        NCDungeonRunId runId,
        NCDungeonRunState state,
        NCDungeonRunResult result,
        DateTime? completedAt,
        string? error,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpdateNCDungeonRunAsync(
            runId, state, result, completedAt, error, expectedVersion));
    }

    public Task<IReadOnlyList<NCDungeonRunData>> GetRecoverableNCDungeonRunsAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetRecoverableNCDungeonRunsAsync());
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCDungeonRunData?> TryCreateNCDungeonRunAsync(NCDungeonRunData run)
    {
        if (!run.RunId.IsValid || !run.SlotId.IsValid || !run.DeploymentId.IsValid ||
            run.Participants.Count == 0 || run.Participants.Any(value => !value.IsValid))
            return null;

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var terminal = new[] { (int) NCDungeonRunState.Closed, (int) NCDungeonRunState.Failed };
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

    public async Task<NCDungeonRunData?> UpdateNCDungeonRunAsync(
        NCDungeonRunId runId,
        NCDungeonRunState state,
        NCDungeonRunResult result,
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

    public async Task<IReadOnlyList<NCDungeonRunData>> GetRecoverableNCDungeonRunsAsync()
    {
        await using var db = await GetDb();
        var records = await db.DbContext.NCDungeonRuns.AsNoTracking()
            .Include(value => value.Participants)
            .Where(value => value.State != (int) NCDungeonRunState.Closed &&
                value.State != (int) NCDungeonRunState.Failed)
            .ToListAsync();
        return records.Select(ToData).ToArray();
    }

    private static NCDungeonRunData ToData(NCDungeonRunRecord record)
    {
        return new NCDungeonRunData(
            new NCDungeonRunId(record.RunId),
            new NCDungeonSlotId(new NCZoneId(record.DungeonSlotId)),
            record.DefinitionId,
            record.TemplateId,
            record.TemplateRevision,
            new NCZoneDeploymentId(record.DeploymentId),
            (NCDungeonRunState) record.State,
            record.Participants.Select(value => new NCCharacterId(value.ProfileId)).ToHashSet(),
            record.Seed,
            record.StartedAt,
            record.CompletedAt,
            (NCDungeonRunResult) record.Result,
            record.Error,
            record.Version);
    }
}
