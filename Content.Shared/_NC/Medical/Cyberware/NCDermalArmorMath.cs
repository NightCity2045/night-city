// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>One authoritative durability pool shared by medical saves and derived regional armor.</summary>
public static class NCDermalArmorMath
{
    public static float Sp(float nominalSp, float integrity, float maximumIntegrity)
    {
        if (!float.IsFinite(nominalSp) || !float.IsFinite(integrity) || !float.IsFinite(maximumIntegrity) || maximumIntegrity <= 0f)
            return 0f;
        return MathF.Max(0f, nominalSp) * Math.Clamp(integrity / maximumIntegrity, 0f, 1f);
    }

    public static float Ablate(float integrity, float maximumIntegrity, float nominalSp, float lostSp)
    {
        if (nominalSp <= 0f || maximumIntegrity <= 0f || lostSp <= 0f) return integrity;
        return MathF.Max(0f, integrity - maximumIntegrity * lostSp / nominalSp);
    }
}
