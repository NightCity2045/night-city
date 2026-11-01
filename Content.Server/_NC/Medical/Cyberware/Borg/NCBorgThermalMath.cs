// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Cyberware.Borg;

/// <summary>Finite coolant removes generated and retained heat toward the physical circuit's setpoint.</summary>
public static class NCBorgThermalMath
{
    public const float Setpoint = 310.15f;

    public static float Advance(float temperature, float elapsedSeconds, float poweredSeconds, bool livingBrain,
        float coolantUnits)
    {
        if (!float.IsFinite(temperature) || !float.IsFinite(elapsedSeconds) || !float.IsFinite(poweredSeconds) ||
            !float.IsFinite(coolantUnits) || elapsedSeconds < 0f || poweredSeconds < 0f || coolantUnits < 0f)
            return temperature;
        var heated = temperature + (livingBrain ? elapsedSeconds : poweredSeconds) * 0.02f;
        var cooling = MathF.Min(MathF.Max(0f, heated - Setpoint), coolantUnits * 4f);
        return Math.Clamp(heated - cooling, 273.15f, 350f);
    }
}
