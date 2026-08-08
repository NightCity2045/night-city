// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Preferences;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

public sealed partial class ServerPreferencesManager
{
    private readonly Dictionary<NetUserId, Dictionary<int, NCCharacterRuntimeData>> _ncCharacterData = new();

    private void InitializeNCEmploymentNetworking()
    {
        _netManager.RegisterNetMessage<NCEmploymentSnapshotMessage>();
        _netManager.RegisterNetMessage<NCResignEmploymentMessage>(HandleNCResignEmployment);
    }

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

        // Lobby clients receive changes made by personnel consoles without reconnecting.
        if (_cachedPlayerPrefs.TryGetValue(userId, out var preferences) && preferences.PrefsLoaded)
            SendNCEmploymentSnapshot(userId);
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

        // Push authoritative changes made after the lobby preferences finished loading.
        if (_cachedPlayerPrefs.TryGetValue(userId, out var preferences) && preferences.PrefsLoaded)
            SendNCEmploymentSnapshot(userId);
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

    private void SendNCEmploymentSnapshot(NetUserId userId)
    {
        if (!_playerManager.TryGetSessionById(userId, out var session) ||
            !_ncCharacterData.TryGetValue(userId, out var characters))
        {
            return;
        }

        var message = new NCEmploymentSnapshotMessage();
        foreach (var (slot, data) in characters)
        {
            if (data.HasEmploymentRecord)
                message.Employment[slot] = data.Job?.Id;
        }

        _netManager.ServerSendMessage(message, session.Channel);
    }

    private async void HandleNCResignEmployment(NCResignEmploymentMessage message)
    {
        if (!_playerManager.TryGetSessionByChannel(message.MsgChannel, out var session) ||
            !TryGetSelectedNCData(session.UserId, out var character) ||
            character.Job == null)
        {
            SendNCEmploymentSnapshot(message.MsgChannel.UserId);
            return;
        }

        if (await _db.ResignNCCharacterEmploymentAsync(session.UserId, character.CharacterId.Value))
        {
            // NC - The old lobby department choice must not silently select or restore the resigned position.
            await ClearSelectedNCDepartmentPreferenceAsync(session.UserId);
            await RefreshNCCharacterData(session.UserId);
        }
        else
            SendNCEmploymentSnapshot(session.UserId);
    }

    /// <summary>
    /// Creates employment from an explicitly selected department.
    /// Terminated employment can only be restored after the player changes and saves the department choice.
    /// </summary>
    private async Task CreateNCEntryEmploymentIfNeeded(
        NetUserId userId,
        int slot,
        ProtoId<DepartmentPrototype>? departmentId,
        bool allowTerminated = false)
    {
        if (departmentId is not { } selectedDepartment ||
            !_ncCharacterData.TryGetValue(userId, out var characters) ||
            !characters.TryGetValue(slot, out var character) ||
            character.Job != null ||
            (character.HasEmploymentRecord && !allowTerminated) ||
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

    /// <summary>
    /// Clears the obsolete lobby preference after a voluntary resignation and synchronizes it to the client.
    /// </summary>
    private async Task ClearSelectedNCDepartmentPreferenceAsync(NetUserId userId)
    {
        if (!_cachedPlayerPrefs.TryGetValue(userId, out var preferences) ||
            preferences.Prefs is not { } playerPreferences ||
            !playerPreferences.Characters.TryGetValue(playerPreferences.SelectedCharacterIndex, out var profile) ||
            profile.NCDepartmentPreference == null)
        {
            return;
        }

        var clearedProfile = profile.WithNCDepartmentPreference(null);
        var characters = new Dictionary<int, HumanoidCharacterProfile>(playerPreferences.Characters)
        {
            [playerPreferences.SelectedCharacterIndex] = clearedProfile,
        };
        preferences.Prefs = new PlayerPreferences(
            characters,
            playerPreferences.SelectedCharacterIndex,
            playerPreferences.AdminOOCColor,
            playerPreferences.ConstructionFavorites);

        await _db.SaveCharacterSlotAsync(userId, clearedProfile, playerPreferences.SelectedCharacterIndex);

        if (!_playerManager.TryGetSessionById(userId, out var session))
            return;

        _netManager.ServerSendMessage(new MsgPreferencesAndSettings
        {
            Preferences = preferences.Prefs,
            Settings = new GameSettings { MaxCharacterSlots = MaxCharacterSlots },
        }, session.Channel);
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
