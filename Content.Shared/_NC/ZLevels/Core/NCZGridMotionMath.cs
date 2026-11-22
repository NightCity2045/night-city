// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.ZLevels.Core;

/// <summary>Visual progress is derived from server time, rather than sent each frame.</summary>
public static class NCZGridMotionMath
{
    public static float Offset(TimeSpan startedAt, TimeSpan arrivesAt, TimeSpan now, int direction)
    {
        var duration = (arrivesAt - startedAt).TotalSeconds;
        if (duration <= 0)
            return direction;

        return direction * (float) Math.Clamp((now - startedAt).TotalSeconds / duration, 0, 1);
    }
}
