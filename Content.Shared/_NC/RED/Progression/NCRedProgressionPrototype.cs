using Robust.Shared.Prototypes;

namespace Content.Shared._NC.RED.Progression;

/// <summary>
/// Data-driven level thresholds and skill-point budget for persistent RED characters.
/// </summary>
[Prototype("ncRedProgression")]
public sealed partial class NCRedProgressionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<int> CompletedRoundThresholds { get; private set; } = [];

    [DataField]
    public int SkillPointsPerLevel { get; private set; } = 10;

    public int GetLevel(int completedRounds)
    {
        var level = 1;
        for (var index = 0; index < CompletedRoundThresholds.Count; index++)
        {
            if (completedRounds < CompletedRoundThresholds[index])
                break;

            level = index + 1;
        }

        return Math.Clamp(level, 1, CompletedRoundThresholds.Count);
    }

    public int GetTotalSkillPoints(int level)
    {
        return Math.Max(level, 1) * SkillPointsPerLevel;
    }
}

/// <summary>
/// One purchasable RED skill. Costs and limits are balance data, never server constants.
/// </summary>
[Prototype("ncRedSkill")]
public sealed partial class NCRedSkillPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField(required: true)]
    public LocId Description { get; private set; }

    [DataField]
    public int MaxRank { get; private set; } = 10;

    [DataField]
    public int CostPerRank { get; private set; } = 1;
}
