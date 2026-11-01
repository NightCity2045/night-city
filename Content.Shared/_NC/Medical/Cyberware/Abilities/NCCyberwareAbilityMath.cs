// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Cyberware.Abilities;

public static class NCCyberwareAbilityMath
{
    public static bool ValidatePolicy(NCCyberwareAbilityPolicyPrototype policy)
        => Unit(policy.MaximumMotorBonus) && Unit(policy.MaximumPenaltyReduction) &&
           Unit(policy.MinimumManualDurationFactor) && policy.MinimumManualDurationFactor > 0f &&
           float.IsFinite(policy.MaximumWalkSpeed) && policy.MaximumWalkSpeed is > 0f and <= 32f &&
           float.IsFinite(policy.MaximumSprintSpeed) && policy.MaximumSprintSpeed is > 0f and <= 32f;
    private static bool Unit(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;
    public static bool ValidCooldown(float value) => float.IsFinite(value) && value is >= 0f and <= 86400f;
    public static float Remaining(float value, TimeSpan observedAt, TimeSpan now)
        => ValidCooldown(value) ? MathF.Max(0f, value - (float)Math.Max(0d, (now - observedAt).TotalSeconds)) : float.NaN;
    public static float RestoreMovingPenalty(float multiplier, float reduction)
        => Math.Clamp(multiplier, 0f, 1f) + (1f - Math.Clamp(multiplier, 0f, 1f)) * Math.Clamp(reduction, 0f, 1f);
    public static float CappedModifier(float modifier, float baseSpeed, float maximum)
        => baseSpeed > 0f && maximum > 0f ? MathF.Min(modifier, maximum / baseSpeed) : modifier;
}
