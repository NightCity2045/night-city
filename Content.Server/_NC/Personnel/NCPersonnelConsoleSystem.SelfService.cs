// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.Database;
using Content.Shared._NC.Identity;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Personnel;

public enum NCPersonnelSelfHireStatus : byte
{
    Success,
    Invalid,
    AlreadyEmployed,
    PositionFull,
    Banned,
    StorageFailure,
}

public sealed partial class NCPersonnelConsoleSystem
{
    [Dependency] private IBanManager _banManager = default!;

    /// <summary>
    /// Applies through the same persistent roster, position cap and live ID update as a personnel console. The caller
    /// supplies the exact public department and role allowlist; management authority is never granted by this route.
    /// </summary>
    public async Task<NCPersonnelSelfHireStatus> TrySelfHireAsync(
        EntityUid user,
        NCCharacterId characterId,
        ProtoId<DepartmentPrototype> departmentId,
        ProtoId<JobPrototype> jobId,
        IReadOnlySet<ProtoId<JobPrototype>> publicJobs)
    {
        if (!_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var selected) || selected != characterId ||
            !publicJobs.Contains(jobId) || !_prototypes.TryIndex(departmentId, out var department) ||
            !_prototypes.TryIndex(jobId, out JobPrototype? job) || !department.Roles.Contains(job.ID) ||
            !department.NCPositionLimits.TryGetValue(job.ID, out var limit))
            return NCPersonnelSelfHireStatus.Invalid;

        if (_banManager.GetJobBans(session.UserId)?.Contains(job.ID) == true)
            return NCPersonnelSelfHireStatus.Banned;

        try
        {
            var actorName = MetaData(user).EntityName;
            var result = await _database.SelfHireNCCharacterAsync(characterId, job.ID, limit,
                actorName, "Trauma Team Agent application");
            if (result.Result != NCPersonnelChangeResult.Success || result.TargetUserId is not { } targetUserId)
            {
                return result.Result switch
                {
                    NCPersonnelChangeResult.CharacterUnavailable => NCPersonnelSelfHireStatus.AlreadyEmployed,
                    NCPersonnelChangeResult.PositionFull => NCPersonnelSelfHireStatus.PositionFull,
                    NCPersonnelChangeResult.InvalidEmployment => NCPersonnelSelfHireStatus.Invalid,
                    _ => NCPersonnelSelfHireStatus.StorageFailure,
                };
            }

            await _preferences.RefreshNCEmploymentAsync(targetUserId);
            UpdateLiveId(targetUserId, characterId, job);
            await RefreshOpenViewsAsync();
            return NCPersonnelSelfHireStatus.Success;
        }
        catch (Exception exception)
        {
            Log.Error($"Self-service hire failed for character {characterId}: {exception}");
            return NCPersonnelSelfHireStatus.StorageFailure;
        }
    }
}
