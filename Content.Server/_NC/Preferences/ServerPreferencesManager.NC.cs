// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Identity;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

public sealed partial class ServerPreferencesManager
{
    private readonly Dictionary<NetUserId, Dictionary<int, NCCharacterRuntimeData>> _ncCharacterData = new();

    private void CacheNCCharacterData(NetUserId userId, Preference preference)
    {
        var characters = new Dictionary<int, NCCharacterRuntimeData>();
        foreach (var profile in preference.Profiles)
        {
            ProtoId<JobPrototype>? job = null;
            var hasEmploymentRecord = profile.NCEmployment != null;
            if (profile.NCEmployment is { State: NCEmploymentState.Active } employment &&
                _prototypeManager.HasIndex<JobPrototype>(employment.JobPrototypeId))
            {
                job = employment.JobPrototypeId;
            }

            characters[profile.Slot] = new NCCharacterRuntimeData(
                new NCCharacterId(profile.Id),
                job,
                hasEmploymentRecord);
        }

        _ncCharacterData[userId] = characters;
    }

    private async Task RefreshNCCharacterData(NetUserId userId)
    {
        var rows = await _db.GetNCCharacterEmploymentDataAsync(userId);
        var characters = new Dictionary<int, NCCharacterRuntimeData>();
        foreach (var row in rows)
        {
            ProtoId<JobPrototype>? job = null;
            if (row.State == NCEmploymentState.Active &&
                row.JobPrototypeId is { } jobId &&
                _prototypeManager.HasIndex<JobPrototype>(jobId))
            {
                job = jobId;
            }

            characters[row.Slot] = new NCCharacterRuntimeData(
                new NCCharacterId(row.ProfileId),
                job,
                row.State != null);
        }

        _ncCharacterData[userId] = characters;
    }

    public bool TryGetSelectedNCCharacterId(NetUserId userId, out NCCharacterId characterId)
    {
        characterId = default;
        if (!TryGetSelectedNCData(userId, out var data))
            return false;

        characterId = data.CharacterId;
        return characterId.IsValid;
    }

    public bool TryGetSelectedNCEmployment(NetUserId userId, out ProtoId<JobPrototype> job)
    {
        job = default;
        if (!TryGetSelectedNCData(userId, out var data) || data.Job is not { } assignedJob)
            return false;

        job = assignedJob;
        return true;
    }

    public async Task<bool> SetSelectedNCEmploymentAsync(NetUserId userId, ProtoId<JobPrototype>? job)
    {
        if (!TryGetSelectedNCData(userId, out var data))
            return false;

        if (!await _db.SetNCCharacterEmploymentAsync(userId, data.CharacterId.Value, job))
            return false;

        await RefreshNCCharacterData(userId);
        return true;
    }

    /// <summary>Refreshes only persistent Night City identity/employment data after an IC personnel action.</summary>
    public Task RefreshNCEmploymentAsync(NetUserId userId)
    {
        return RefreshNCCharacterData(userId);
    }

    /// <summary>
    /// Creates the character's first employment from the selected department.
    /// A previous record, including a terminated one, can only be changed by the personnel workflow.
    /// </summary>
    private async Task CreateNCEntryEmploymentIfNeeded(
        NetUserId userId,
        int slot,
        ProtoId<DepartmentPrototype>? departmentId)
    {
        if (departmentId is not { } selectedDepartment ||
            !_ncCharacterData.TryGetValue(userId, out var characters) ||
            !characters.TryGetValue(slot, out var character) ||
            character.HasEmploymentRecord ||
            !_prototypeManager.TryIndex(selectedDepartment, out var department) ||
            !department.NCSelectable ||
            department.NCEntryJob is not { } entryJob ||
            !_prototypeManager.HasIndex(entryJob))
        {
            return;
        }

        if (await _db.SetNCCharacterEmploymentAsync(userId, character.CharacterId.Value, entryJob))
            await RefreshNCCharacterData(userId);
    }

    private bool TryGetSelectedNCData(NetUserId userId, out NCCharacterRuntimeData data)
    {
        data = default;
        return _cachedPlayerPrefs.TryGetValue(userId, out var preferences) &&
               preferences.Prefs is { } playerPreferences &&
               _ncCharacterData.TryGetValue(userId, out var characters) &&
               characters.TryGetValue(playerPreferences.SelectedCharacterIndex, out data);
    }

    private readonly record struct NCCharacterRuntimeData(
        NCCharacterId CharacterId,
        ProtoId<JobPrototype>? Job,
        bool HasEmploymentRecord);
}
