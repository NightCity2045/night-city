using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.RED.Progression;

[Serializable, NetSerializable]
public sealed class NCAllocateSkillRequest : EntityEventArgs
{
    public string SkillPrototypeId { get; }
    public int TargetRank { get; }
    public Guid RequestId { get; }

    public NCAllocateSkillRequest(string skillPrototypeId, int targetRank, Guid requestId)
    {
        SkillPrototypeId = skillPrototypeId;
        TargetRank = targetRank;
        RequestId = requestId;
    }
}

[Serializable, NetSerializable]
public sealed class NCProgressionStateEvent : EntityEventArgs
{
    public int CompletedRounds { get; }
    public byte Level { get; }
    public int SpentSkillPoints { get; }
    public int TotalSkillPoints { get; }
    public IReadOnlyDictionary<string, int> Skills { get; }
    public long BankBalance { get; }
    public int PropertyCount { get; }
    public int BusinessCount { get; }
    public string? PositionPrototypeId { get; }
    public byte LifecycleStatus { get; }
    public string? Error { get; }

    public NCProgressionStateEvent(
        int completedRounds,
        byte level,
        int spentSkillPoints,
        int totalSkillPoints,
        IReadOnlyDictionary<string, int> skills,
        long bankBalance,
        int propertyCount,
        int businessCount,
        string? positionPrototypeId,
        byte lifecycleStatus,
        string? error = null)
    {
        CompletedRounds = completedRounds;
        Level = level;
        SpentSkillPoints = spentSkillPoints;
        TotalSkillPoints = totalSkillPoints;
        Skills = skills;
        BankBalance = bankBalance;
        PropertyCount = propertyCount;
        BusinessCount = businessCount;
        PositionPrototypeId = positionPrototypeId;
        LifecycleStatus = lifecycleStatus;
        Error = error;
    }
}
