// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Skill;

public static class NCSkillProgression
{
    private static readonly int[] LevelThresholds = [0, 1, 3, 6, 10, 15, 21, 28, 36, 45];

    public static int Experience(long activeTicks) => (int) (activeTicks / TimeSpan.TicksPerHour);

    public static int Level(int experience)
    {
        for (var i = LevelThresholds.Length - 1; i > 0; i--)
        {
            if (experience >= LevelThresholds[i])
                return i + 1;
        }

        return 1;
    }

    public static int Points(int experience, int purchases) => Math.Max(0, 5 + (Level(experience) - 1) * 5 - purchases);
    public static int LevelThreshold(int level) => LevelThresholds[level - 1];
    public static int? NextThreshold(int level) => level < LevelThresholds.Length ? LevelThresholds[level] : null;
}

[ByRefEvent]
public record struct NCWeaponUseAttemptEvent(EntityUid User, bool Cancelled = false)
{
    public static bool CanUse(IEntityManager entities, EntityUid weapon, EntityUid? user)
    {
        if (user is not { } actor || !entities.HasComponent<NCRequiredSkillComponent>(weapon))
            return true;

        var attempt = new NCWeaponUseAttemptEvent(actor);
        entities.EventBus.RaiseLocalEvent(weapon, ref attempt);
        return !attempt.Cancelled;
    }
}
