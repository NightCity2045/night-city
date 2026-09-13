// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Vehicle;

/// <summary>Pure crash-response math shared with content tests.</summary>
public static class NCVehicleDamageMath
{
    /// <summary>Returns injury delta-V only when the chassis itself moves into the pedestrian.</summary>
    public static float PedestrianImpact(float chassisClosingSpeed, float minimumSpeed, float transfer)
    {
        if (!float.IsFinite(chassisClosingSpeed) || !float.IsFinite(minimumSpeed) || !float.IsFinite(transfer) ||
            chassisClosingSpeed <= minimumSpeed || minimumSpeed < 0 || transfer < 0)
            return 0;
        return chassisClosingSpeed * transfer;
    }

    public static float OccupantTrauma(float deltaV, float transfer, float threshold, float traumaPerDeltaV, float maximum)
    {
        if (!float.IsFinite(deltaV) || !float.IsFinite(transfer) || !float.IsFinite(threshold) ||
            !float.IsFinite(traumaPerDeltaV) || !float.IsFinite(maximum) || deltaV <= 0 || transfer < 0 ||
            threshold < 0 || traumaPerDeltaV < 0 || maximum <= 0)
            return 0;
        return Math.Min(Math.Max(0, deltaV * transfer - threshold) * traumaPerDeltaV, maximum);
    }

    /// <summary>A single non-catastrophic collision can impair a module, while repeated hits can still destroy it.</summary>
    public static float ModuleConditionLoss(float energy, float capacity, int count, float deltaV,
        float catastrophicDeltaV, float nonCatastrophicMaximum)
    {
        if (!float.IsFinite(energy) || !float.IsFinite(capacity) || energy <= 0 || capacity <= 0 || count <= 0)
            return 0;
        var loss = energy / capacity / count;
        if (deltaV > 0 && deltaV < catastrophicDeltaV)
            loss = Math.Min(loss, nonCatastrophicMaximum);
        return Math.Max(0, loss);
    }
}
