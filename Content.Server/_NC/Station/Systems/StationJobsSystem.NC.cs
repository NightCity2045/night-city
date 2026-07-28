using Content.Server.Station.Components;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Roles;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.Station.Systems;

/// <summary>
/// Keeps the upstream job infrastructure intact while presenting every ordinary
/// Night City entrant as a technical Citizen.
/// </summary>
public sealed partial class StationJobsSystem
{
    private partial void ConfigureNCJobs(StationJobsComponent jobs)
    {
        if (!_configurationManager.GetCVar(NCCVars.SingleCitizenJob))
            return;

        // Citizen is unlimited at round start and for late join on every playable station.
        jobs.SetupAvailableJobs[NCJobIds.Citizen] = [-1, -1];
    }

    private partial bool TryAssignNCCitizenJobs(
        IReadOnlyDictionary<NetUserId, HumanoidCharacterProfile> profiles,
        IReadOnlyList<EntityUid> stations,
        out Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)> assigned)
    {
        assigned = new Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>(profiles.Count);

        if (!_configurationManager.GetCVar(NCCVars.SingleCitizenJob))
            return false;

        if (stations.Count == 0)
            return true;

        // Rule-controlled players have already been removed from this collection by GameTicker.
        // The remaining players are ordinary entrants and retain the normal Job/MindRole spawn flow.
        foreach (var userId in profiles.Keys)
            assigned[userId] = (NCJobIds.Citizen, _random.Pick(stations));

        return true;
    }
}
