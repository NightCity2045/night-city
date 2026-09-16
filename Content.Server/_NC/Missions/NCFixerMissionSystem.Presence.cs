// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Missions;
using Robust.Shared.Timing;

namespace Content.Server._NC.Missions;

public sealed partial class NCFixerMissionSystem
{
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<Guid, Attendance> _attendance = [];
    private readonly List<Guid> _expiredAttendance = [];

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_attendance.Count == 0 || !_clock.TryGetCurrentTime(out _, out var now)) return;
        _expiredAttendance.Clear();
        foreach (var (runId, attendance) in _attendance)
        {
            if (now.Ticks >= attendance.Deadline)
            {
                _expiredAttendance.Add(runId);
                continue;
            }
            if (_timing.CurTime < attendance.NextCheck) continue;
            attendance.NextCheck = _timing.CurTime + attendance.Interval;
            if (!_missions.TryGetRun(new(runId), out var run) || run.State != NCMissionRunState.Active) continue;
            // Only connected participants in known deployments are sampled; no world-wide ECS scan or PVS dependency.
            foreach (var session in _players.Sessions)
            {
                if (session.AttachedEntity is not { } body ||
                    !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var character) ||
                    !attendance.Crew.Contains(character.Value) || attendance.Present.Contains(character.Value) ||
                    !_mobState.IsAlive(body) || !_coordinates.TryGetCoordinates(body, out var position) ||
                    !_zones.Contains(run.SlotId.ZoneId, position)) continue;
                attendance.Present.Add(character.Value);
            }
        }
        foreach (var id in _expiredAttendance) _attendance.Remove(id);
    }

    private sealed class Attendance(List<int> crew, TimeSpan interval, long deadline)
    {
        public readonly HashSet<int> Crew = crew.ToHashSet();
        public readonly HashSet<int> Present = [];
        public readonly TimeSpan Interval = interval;
        public readonly long Deadline = deadline;
        public TimeSpan NextCheck;
    }
}
