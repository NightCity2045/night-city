using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCSkillSpendResult> SpendNCSkillPointsAsync(
        int profileId,
        string skillPrototypeId,
        int targetRank,
        int maxRank,
        int costPerRank,
        int skillPointsPerLevel,
        Guid requestId,
        int? roundId);

    Task<NCParticipationResult> AddNCActiveSecondsAsync(
        int profileId,
        Guid accountId,
        int roundId,
        int seconds,
        int creditThreshold,
        IReadOnlyList<int> levelThresholds,
        Guid requestId);
}

public sealed partial class ServerDbManager
{
    public Task<NCSkillSpendResult> SpendNCSkillPointsAsync(
        int profileId,
        string skillPrototypeId,
        int targetRank,
        int maxRank,
        int costPerRank,
        int skillPointsPerLevel,
        Guid requestId,
        int? roundId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SpendNCSkillPointsAsync(
            profileId,
            skillPrototypeId,
            targetRank,
            maxRank,
            costPerRank,
            skillPointsPerLevel,
            requestId,
            roundId));
    }

    public Task<NCParticipationResult> AddNCActiveSecondsAsync(
        int profileId,
        Guid accountId,
        int roundId,
        int seconds,
        int creditThreshold,
        IReadOnlyList<int> levelThresholds,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.AddNCActiveSecondsAsync(
            profileId,
            accountId,
            roundId,
            seconds,
            creditThreshold,
            levelThresholds,
            requestId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCSkillSpendResult> SpendNCSkillPointsAsync(
        int profileId,
        string skillPrototypeId,
        int targetRank,
        int maxRank,
        int costPerRank,
        int skillPointsPerLevel,
        Guid requestId,
        int? roundId)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        var progression = await db.DbContext.NCCharacterProgression
            .SingleAsync(entry => entry.ProfileId == profileId);
        var existing = await db.DbContext.NCCharacterSkill
            .SingleOrDefaultAsync(entry =>
                entry.ProfileId == profileId &&
                entry.SkillPrototypeId == skillPrototypeId);

        var currentRank = existing?.Rank ?? 0;
        var totalPoints = progression.Level * skillPointsPerLevel;
        if (targetRank <= currentRank || targetRank > maxRank || costPerRank <= 0)
            return new NCSkillSpendResult(false, "invalid-rank", currentRank, progression.SpentSkillPoints, totalPoints);

        var cost = checked((targetRank - currentRank) * costPerRank);
        if (progression.SpentSkillPoints + cost > totalPoints)
            return new NCSkillSpendResult(false, "insufficient-points", currentRank, progression.SpentSkillPoints, totalPoints);

        var now = DateTime.UtcNow;
        existing ??= new NCCharacterSkill
        {
            ProfileId = profileId,
            SkillPrototypeId = skillPrototypeId,
        };
        if (db.DbContext.Entry(existing).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            db.DbContext.NCCharacterSkill.Add(existing);

        existing.Rank = targetRank;
        existing.SpentPoints += cost;
        existing.UpdatedAt = now;
        progression.SpentSkillPoints += cost;
        progression.UpdatedAt = now;

        db.DbContext.NCPersistenceAudit.Add(new NCPersistenceAudit
        {
            Timestamp = now,
            RoundId = roundId,
            TargetProfileId = profileId,
            Action = "skill-spend",
            OldValue = $"{skillPrototypeId}:{currentRank}",
            NewValue = $"{skillPrototypeId}:{targetRank}",
            Reason = "Character skill allocation",
            RequestId = requestId,
        });

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCSkillSpendResult(true, null, targetRank, progression.SpentSkillPoints, totalPoints);
    }

    public async Task<NCParticipationResult> AddNCActiveSecondsAsync(
        int profileId,
        Guid accountId,
        int roundId,
        int seconds,
        int creditThreshold,
        IReadOnlyList<int> levelThresholds,
        Guid requestId)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        var now = DateTime.UtcNow;
        var participation = await db.DbContext.NCCharacterRoundParticipation
            .SingleOrDefaultAsync(entry => entry.ProfileId == profileId && entry.RoundId == roundId);
        if (participation == null)
        {
            participation = new NCCharacterRoundParticipation
            {
                ProfileId = profileId,
                AccountId = accountId,
                RoundId = roundId,
                FirstJoinedAt = now,
                LastSeenAt = now,
            };
            db.DbContext.NCCharacterRoundParticipation.Add(participation);
        }

        participation.ActiveSeconds = checked(participation.ActiveSeconds + Math.Max(seconds, 0));
        participation.LastSeenAt = now;

        var progression = await db.DbContext.NCCharacterProgression
            .SingleAsync(entry => entry.ProfileId == profileId);
        var oldLevel = progression.Level;
        var newlyCounted = false;

        if (!participation.Counted && participation.ActiveSeconds >= creditThreshold)
        {
            var accountCredit = await db.DbContext.NCRoundAccountCredit
                .SingleOrDefaultAsync(entry => entry.AccountId == accountId && entry.RoundId == roundId);
            if (accountCredit == null)
            {
                accountCredit = new NCRoundAccountCredit
                {
                    AccountId = accountId,
                    RoundId = roundId,
                    CreditedProfileId = profileId,
                    CreditedAt = now,
                };
                db.DbContext.NCRoundAccountCredit.Add(accountCredit);

                participation.Counted = true;
                participation.CountedAt = now;
                progression.CompletedRounds++;
                progression.LastCountedRoundId = roundId;
                progression.Level = CalculateLevel(progression.CompletedRounds, levelThresholds);
                progression.UpdatedAt = now;
                newlyCounted = true;

                db.DbContext.NCPersistenceAudit.Add(new NCPersistenceAudit
                {
                    Timestamp = now,
                    RoundId = roundId,
                    ActorAccountId = accountId,
                    TargetProfileId = profileId,
                    Action = "round-credit",
                    OldValue = (progression.CompletedRounds - 1).ToString(),
                    NewValue = progression.CompletedRounds.ToString(),
                    Reason = "Active play threshold reached",
                    RequestId = requestId,
                });
            }
        }

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return new NCParticipationResult(
            participation.ActiveSeconds,
            participation.Counted,
            newlyCounted,
            progression.CompletedRounds,
            progression.Level,
            progression.Level > oldLevel);
    }

    private static byte CalculateLevel(int completedRounds, IReadOnlyList<int> thresholds)
    {
        var level = 1;
        for (var index = 0; index < thresholds.Count; index++)
        {
            if (completedRounds < thresholds[index])
                break;

            level = index + 1;
        }

        return (byte) Math.Clamp(level, 1, thresholds.Count);
    }
}
