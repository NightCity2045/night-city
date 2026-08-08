// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Identity;
using Content.Shared.Database._NC.Police;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCPoliceCaseSubjectData(
    NCCharacterId CharacterId,
    string CharacterName,
    NCPoliceCaseSubjectRole Role);

public sealed record NCPoliceCaseEntryData(
    long Id,
    NCPoliceCaseEntryType EntryType,
    string Text,
    NCPoliceCaseStatus? PreviousStatus,
    NCPoliceCaseStatus? NewStatus,
    NCCharacterId? SubjectCharacterId,
    string? SubjectName,
    NCPoliceCaseSubjectRole? SubjectRole,
    string AuthorName,
    DateTime CreatedAt);

public sealed record NCPoliceCaseData(
    long Id,
    string Title,
    string Summary,
    NCPoliceCaseStatus Status,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<NCPoliceCaseSubjectData> Subjects,
    IReadOnlyList<NCPoliceCaseEntryData> Entries);

public sealed record NCPoliceWarrantData(
    long Id,
    long? CaseId,
    NCCharacterId TargetCharacterId,
    string TargetName,
    NCPoliceWarrantType Type,
    NCPoliceWarrantStatus Status,
    string Reason,
    string IssuedByName,
    DateTime IssuedAt,
    string? ResolvedByName,
    string? ResolutionReason,
    DateTime? ResolvedAt);

public partial interface IServerDbManager
{
    Task<IReadOnlyList<NCPoliceCaseData>> GetNCPoliceCasesAsync(int limit);
    Task<NCPoliceCaseData?> GetNCPoliceCaseAsync(long caseId, int entryLimit);
    Task<NCPoliceCaseData?> CreateNCPoliceCaseAsync(
        string title,
        string summary,
        NCCharacterId initialSubject,
        NCCharacterId actorCharacterId,
        string actorName);
    Task<NCPoliceCaseData?> AddNCPoliceCaseSubjectAsync(
        long caseId,
        NCCharacterId subject,
        NCPoliceCaseSubjectRole role,
        NCCharacterId actorCharacterId,
        string actorName);
    Task<NCPoliceCaseData?> AddNCPoliceCaseEntryAsync(
        long caseId,
        string text,
        NCCharacterId actorCharacterId,
        string actorName);
    Task<NCPoliceCaseData?> SetNCPoliceCaseStatusAsync(
        long caseId,
        NCPoliceCaseStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName);
    Task<IReadOnlyList<NCPoliceWarrantData>> GetNCPoliceWarrantsAsync(int limit);
    Task<NCPoliceWarrantData?> CreateNCPoliceWarrantAsync(
        NCCharacterId target,
        long? caseId,
        NCPoliceWarrantType type,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName);
    Task<NCPoliceWarrantData?> ResolveNCPoliceWarrantAsync(
        long warrantId,
        NCPoliceWarrantStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName);
}

public sealed partial class ServerDbManager
{
    public Task<IReadOnlyList<NCPoliceCaseData>> GetNCPoliceCasesAsync(int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceCasesAsync(limit));
    }

    public Task<NCPoliceCaseData?> GetNCPoliceCaseAsync(long caseId, int entryLimit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceCaseAsync(caseId, entryLimit));
    }

    public Task<NCPoliceCaseData?> CreateNCPoliceCaseAsync(
        string title,
        string summary,
        NCCharacterId initialSubject,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCPoliceCaseAsync(
            title, summary, initialSubject, actorCharacterId, actorName));
    }

    public Task<NCPoliceCaseData?> AddNCPoliceCaseSubjectAsync(
        long caseId,
        NCCharacterId subject,
        NCPoliceCaseSubjectRole role,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCPoliceCaseSubjectAsync(
            caseId, subject, role, actorCharacterId, actorName));
    }

    public Task<NCPoliceCaseData?> AddNCPoliceCaseEntryAsync(
        long caseId,
        string text,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCPoliceCaseEntryAsync(
            caseId, text, actorCharacterId, actorName));
    }

    public Task<NCPoliceCaseData?> SetNCPoliceCaseStatusAsync(
        long caseId,
        NCPoliceCaseStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPoliceCaseStatusAsync(
            caseId, status, reason, actorCharacterId, actorName));
    }

    public Task<IReadOnlyList<NCPoliceWarrantData>> GetNCPoliceWarrantsAsync(int limit)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPoliceWarrantsAsync(limit));
    }

    public Task<NCPoliceWarrantData?> CreateNCPoliceWarrantAsync(
        NCCharacterId target,
        long? caseId,
        NCPoliceWarrantType type,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.CreateNCPoliceWarrantAsync(
            target, caseId, type, reason, actorCharacterId, actorName));
    }

    public Task<NCPoliceWarrantData?> ResolveNCPoliceWarrantAsync(
        long warrantId,
        NCPoliceWarrantStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ResolveNCPoliceWarrantAsync(
            warrantId, status, reason, actorCharacterId, actorName));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<IReadOnlyList<NCPoliceCaseData>> GetNCPoliceCasesAsync(int limit)
    {
        await using var db = await GetDb();
        var cases = await db.DbContext.NCPoliceCases
            .AsNoTracking()
            .Include(value => value.Subjects)
            .OrderByDescending(value => value.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
        return cases.Select(value => ToCaseData(value, Array.Empty<NCPoliceCaseEntry>())).ToList();
    }

    public async Task<NCPoliceCaseData?> GetNCPoliceCaseAsync(long caseId, int entryLimit)
    {
        if (caseId <= 0)
            return null;

        await using var db = await GetDb();
        var policeCase = await db.DbContext.NCPoliceCases
            .AsNoTracking()
            .Include(value => value.Subjects)
            .SingleOrDefaultAsync(value => value.Id == caseId);
        if (policeCase == null)
            return null;

        var entries = await db.DbContext.NCPoliceCaseEntries
            .AsNoTracking()
            .Where(value => value.CaseId == caseId)
            .OrderByDescending(value => value.CreatedAt)
            .Take(Math.Clamp(entryLimit, 1, 200))
            .ToListAsync();
        return ToCaseData(policeCase, entries);
    }

    public async Task<NCPoliceCaseData?> CreateNCPoliceCaseAsync(
        string title,
        string summary,
        NCCharacterId initialSubject,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        var normalizedTitle = title.Trim();
        var normalizedSummary = summary.Trim();
        var normalizedActor = actorName.Trim();
        if (!initialSubject.IsValid || !actorCharacterId.IsValid || normalizedTitle.Length is < 1 or > 128 ||
            normalizedSummary.Length is < 1 or > 1024 || normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var subject = await db.DbContext.Profile.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == initialSubject.Value);
        if (subject == null)
            return null;

        var now = DateTime.UtcNow;
        var policeCase = new NCPoliceCase
        {
            Title = normalizedTitle,
            Summary = normalizedSummary,
            Status = NCPoliceCaseStatus.Open,
            CreatedByProfileId = actorCharacterId.Value,
            CreatedByName = normalizedActor,
            CreatedAt = now,
            UpdatedAt = now,
            Subjects =
            {
                new NCPoliceCaseSubject
                {
                    ProfileId = subject.Id,
                    CharacterName = subject.CharacterName,
                    Role = NCPoliceCaseSubjectRole.PersonOfInterest,
                },
            },
        };
        db.DbContext.NCPoliceCases.Add(policeCase);
        await db.DbContext.SaveChangesAsync();
        return ToCaseData(policeCase, Array.Empty<NCPoliceCaseEntry>());
    }

    public async Task<NCPoliceCaseData?> AddNCPoliceCaseSubjectAsync(
        long caseId,
        NCCharacterId subjectId,
        NCPoliceCaseSubjectRole role,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        if (caseId <= 0 || !subjectId.IsValid || !actorCharacterId.IsValid || !Enum.IsDefined(role))
            return null;

        var normalizedActor = actorName.Trim();
        if (normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var policeCase = await db.DbContext.NCPoliceCases
            .Include(value => value.Subjects)
            .SingleOrDefaultAsync(value => value.Id == caseId);
        var subject = await db.DbContext.Profile.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == subjectId.Value);
        if (policeCase == null || subject == null || policeCase.Status == NCPoliceCaseStatus.Archived)
            return null;

        var existing = policeCase.Subjects.FirstOrDefault(value => value.ProfileId == subject.Id);
        if (existing != null)
            existing.Role = role;
        else
            policeCase.Subjects.Add(new NCPoliceCaseSubject
            {
                ProfileId = subject.Id,
                CharacterName = subject.CharacterName,
                Role = role,
            });

        var now = DateTime.UtcNow;
        policeCase.UpdatedAt = now;
        policeCase.Entries.Add(new NCPoliceCaseEntry
        {
            EntryType = NCPoliceCaseEntryType.SubjectAdded,
            SubjectProfileId = subject.Id,
            SubjectName = subject.CharacterName,
            SubjectRole = role,
            AuthorProfileId = actorCharacterId.Value,
            AuthorName = normalizedActor,
            CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        return await GetNCPoliceCaseAsync(caseId, 200);
    }

    public async Task<NCPoliceCaseData?> AddNCPoliceCaseEntryAsync(
        long caseId,
        string text,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        var normalizedText = text.Trim();
        var normalizedActor = actorName.Trim();
        if (caseId <= 0 || !actorCharacterId.IsValid || normalizedText.Length is < 1 or > 1024 ||
            normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var policeCase = await db.DbContext.NCPoliceCases.SingleOrDefaultAsync(value => value.Id == caseId);
        if (policeCase == null || policeCase.Status == NCPoliceCaseStatus.Archived)
            return null;

        var now = DateTime.UtcNow;
        policeCase.UpdatedAt = now;
        db.DbContext.NCPoliceCaseEntries.Add(new NCPoliceCaseEntry
        {
            CaseId = caseId,
            EntryType = NCPoliceCaseEntryType.Report,
            Text = normalizedText,
            AuthorProfileId = actorCharacterId.Value,
            AuthorName = normalizedActor,
            CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        return await GetNCPoliceCaseAsync(caseId, 200);
    }

    public async Task<NCPoliceCaseData?> SetNCPoliceCaseStatusAsync(
        long caseId,
        NCPoliceCaseStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        var normalizedReason = reason.Trim();
        var normalizedActor = actorName.Trim();
        if (caseId <= 0 || !actorCharacterId.IsValid || !Enum.IsDefined(status) ||
            normalizedReason.Length is < 1 or > 1024 || normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var policeCase = await db.DbContext.NCPoliceCases.SingleOrDefaultAsync(value => value.Id == caseId);
        if (policeCase == null || policeCase.Status == status || policeCase.Status == NCPoliceCaseStatus.Archived)
            return null;

        var previous = policeCase.Status;
        var now = DateTime.UtcNow;
        policeCase.Status = status;
        policeCase.UpdatedAt = now;
        db.DbContext.NCPoliceCaseEntries.Add(new NCPoliceCaseEntry
        {
            CaseId = caseId,
            EntryType = NCPoliceCaseEntryType.StatusChanged,
            Text = normalizedReason,
            PreviousStatus = previous,
            NewStatus = status,
            AuthorProfileId = actorCharacterId.Value,
            AuthorName = normalizedActor,
            CreatedAt = now,
        });
        await db.DbContext.SaveChangesAsync();
        return await GetNCPoliceCaseAsync(caseId, 200);
    }

    public async Task<IReadOnlyList<NCPoliceWarrantData>> GetNCPoliceWarrantsAsync(int limit)
    {
        await using var db = await GetDb();
        var warrants = await db.DbContext.NCPoliceWarrants
            .AsNoTracking()
            .OrderBy(value => value.Status == NCPoliceWarrantStatus.Active ? 0 : 1)
            .ThenByDescending(value => value.IssuedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync();
        return warrants.Select(ToWarrantData).ToList();
    }

    public async Task<NCPoliceWarrantData?> CreateNCPoliceWarrantAsync(
        NCCharacterId target,
        long? caseId,
        NCPoliceWarrantType type,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        var normalizedReason = reason.Trim();
        var normalizedActor = actorName.Trim();
        if (!target.IsValid || !actorCharacterId.IsValid || !Enum.IsDefined(type) ||
            normalizedReason.Length is < 1 or > 512 || normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var targetProfile = await db.DbContext.Profile.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == target.Value);
        if (targetProfile == null || caseId is > 0 &&
            !await db.DbContext.NCPoliceCases.AnyAsync(value => value.Id == caseId.Value))
            return null;

        var warrant = new NCPoliceWarrant
        {
            CaseId = caseId is > 0 ? caseId : null,
            TargetProfileId = target.Value,
            TargetName = targetProfile.CharacterName,
            Type = type,
            Status = NCPoliceWarrantStatus.Active,
            Reason = normalizedReason,
            IssuedByProfileId = actorCharacterId.Value,
            IssuedByName = normalizedActor,
            IssuedAt = DateTime.UtcNow,
        };
        db.DbContext.NCPoliceWarrants.Add(warrant);
        await db.DbContext.SaveChangesAsync();
        return ToWarrantData(warrant);
    }

    public async Task<NCPoliceWarrantData?> ResolveNCPoliceWarrantAsync(
        long warrantId,
        NCPoliceWarrantStatus status,
        string reason,
        NCCharacterId actorCharacterId,
        string actorName)
    {
        var normalizedReason = reason.Trim();
        var normalizedActor = actorName.Trim();
        if (warrantId <= 0 || status == NCPoliceWarrantStatus.Active || !Enum.IsDefined(status) ||
            !actorCharacterId.IsValid || normalizedReason.Length is < 1 or > 512 ||
            normalizedActor.Length is < 1 or > 128)
            return null;

        await using var db = await GetDb();
        var warrant = await db.DbContext.NCPoliceWarrants.SingleOrDefaultAsync(value => value.Id == warrantId);
        if (warrant == null || warrant.Status != NCPoliceWarrantStatus.Active)
            return null;

        warrant.Status = status;
        warrant.ResolvedByProfileId = actorCharacterId.Value;
        warrant.ResolvedByName = normalizedActor;
        warrant.ResolutionReason = normalizedReason;
        warrant.ResolvedAt = DateTime.UtcNow;
        await db.DbContext.SaveChangesAsync();
        return ToWarrantData(warrant);
    }

    private static NCPoliceCaseData ToCaseData(NCPoliceCase value, IEnumerable<NCPoliceCaseEntry> entries)
    {
        return new NCPoliceCaseData(
            value.Id,
            value.Title,
            value.Summary,
            value.Status,
            value.CreatedByName,
            value.CreatedAt,
            value.UpdatedAt,
            value.Subjects.Select(subject => new NCPoliceCaseSubjectData(
                new NCCharacterId(subject.ProfileId), subject.CharacterName, subject.Role)).ToList(),
            entries.Select(entry => new NCPoliceCaseEntryData(
                entry.Id,
                entry.EntryType,
                entry.Text,
                entry.PreviousStatus,
                entry.NewStatus,
                entry.SubjectProfileId is { } subjectId ? new NCCharacterId(subjectId) : null,
                entry.SubjectName,
                entry.SubjectRole,
                entry.AuthorName,
                entry.CreatedAt)).ToList());
    }

    private static NCPoliceWarrantData ToWarrantData(NCPoliceWarrant value)
    {
        return new NCPoliceWarrantData(
            value.Id,
            value.CaseId,
            new NCCharacterId(value.TargetProfileId),
            value.TargetName,
            value.Type,
            value.Status,
            value.Reason,
            value.IssuedByName,
            value.IssuedAt,
            value.ResolvedByName,
            value.ResolutionReason,
            value.ResolvedAt);
    }
}
