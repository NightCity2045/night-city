// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Preferences.Managers;
using Content.Server.Station.Components;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using System.Linq;

namespace Content.Server.Station.Systems;

public sealed partial class StationJobsSystem
{
    [Dependency] private IServerPreferencesManager _ncPreferences = default!;

    /// <summary>
    /// Makes all Night City positions available to the server. Availability here is unlimited because
    /// hiring authority lives in persistent employment, not in a per-round slot picker.
    /// </summary>
    private void ConfigureNCJobs(StationJobsComponent stationJobs)
    {
        foreach (var department in ProtoMan.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!department.NCSelectable)
                continue;

            foreach (var job in department.Roles)
                stationJobs.SetupAvailableJobs.TryAdd(job, [-1, -1]);
        }
    }

    /// <summary>
    /// Recreates each character's authoritative employment at round start.
    /// Characters without active employment always enter as residents.
    /// </summary>
    private bool TryAssignNCJobs(
        Dictionary<NetUserId, HumanoidCharacterProfile> profiles,
        IReadOnlyList<EntityUid> stations,
        out Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)> assigned)
    {
        assigned = new Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>(profiles.Count);
        if (!ProtoMan.EnumeratePrototypes<DepartmentPrototype>().Any(department => department.NCSelectable))
            return false;

        foreach (var userId in profiles.Keys)
        {
            var job = SharedGameTicker.FallbackOverflowJob;
            if (_ncPreferences.TryGetSelectedNCEmployment(userId, out var employment) &&
                IsNCJob(employment) &&
                !(_banManager.GetJobBans(userId)?.Contains(employment) ?? false))
            {
                job = employment;
            }

            assigned[userId] = (job, _random.Pick(stations));
        }

        return true;
    }

    private bool IsNCJob(ProtoId<JobPrototype> job)
    {
        return ProtoMan.EnumeratePrototypes<DepartmentPrototype>()
            .Any(department => department.NCSelectable && department.Roles.Contains(job));
    }
}
