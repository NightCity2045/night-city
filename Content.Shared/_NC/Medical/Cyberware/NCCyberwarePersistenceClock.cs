// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Cyberware;

public static class NCCyberwarePersistenceClock
{
    public static bool TryRebaseDeadline(TimeSpan deadline, TimeSpan capturedAt, TimeSpan runtime, out TimeSpan rebased)
    {
        rebased = default;
        if (capturedAt < TimeSpan.Zero || runtime < TimeSpan.Zero) return false;
        try
        {
            var remaining = deadline > capturedAt ? deadline - capturedAt : TimeSpan.Zero;
            rebased = runtime + remaining;
            return true;
        }
        catch (OverflowException) { return false; }
    }
}
