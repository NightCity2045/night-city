// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.ComponentModel;
using System.Diagnostics;

namespace NightCity.Diagnostics.Contracts;

/// <summary>Optional orphan protection for disposable owned runtimes. PID and start time prevent PID-reuse mistakes.</summary>
public sealed class DiagnosticParentLifetime(int processId, long startedAtUtcTicks)
{
    private long _lastCheck;
    private bool _alive = true;

    public static DiagnosticParentLifetime? FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1" ||
            !int.TryParse(Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_OWNER_PID"), out var pid) || pid <= 0 ||
            !long.TryParse(Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_OWNER_START_TICKS"), out var ticks) || ticks <= 0)
            return null;
        return new DiagnosticParentLifetime(pid, ticks);
    }

    public bool IsAlive()
    {
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck).TotalSeconds < 2)
            return _alive;
        _lastCheck = Stopwatch.GetTimestamp();
        try
        {
            using var process = Process.GetProcessById(processId);
            return _alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startedAtUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return _alive = false; }
        catch (Win32Exception) { return true; } // Access denial is not evidence that the owner died.
    }
}
