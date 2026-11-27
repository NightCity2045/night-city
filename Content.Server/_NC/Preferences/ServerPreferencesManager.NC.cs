// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Identity;
using Content.Shared.Preferences;
using Robust.Shared.Network;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

/// <summary>
/// NC: Caches the stable database identity of each character slot. Roles are the vanilla job priorities, so this
/// only maps a slot to the persistent character ID used by skills, medicine and round snapshots.
/// </summary>
public sealed partial class ServerPreferencesManager
{
    private readonly Dictionary<NetUserId, Dictionary<int, NCCharacterId>> _ncCharacterIds = new();

    private void CacheNCCharacterData(NetUserId userId, Preference preference)
    {
        var characters = new Dictionary<int, NCCharacterId>();
        foreach (var profile in preference.Profiles)
            characters[profile.Slot] = new NCCharacterId(profile.Id);

        _ncCharacterIds[userId] = characters;
    }

    private async Task RefreshNCCharacterData(NetUserId userId)
    {
        var rows = await _db.GetNCCharacterSlotsAsync(userId);
        var characters = new Dictionary<int, NCCharacterId>();
        foreach (var row in rows)
            characters[row.Slot] = new NCCharacterId(row.ProfileId);

        _ncCharacterIds[userId] = characters;
    }

    public bool TryGetSelectedNCCharacterId(NetUserId userId, out NCCharacterId characterId)
    {
        characterId = default;
        return _cachedPlayerPrefs.TryGetValue(userId, out var preferences) &&
               preferences.Prefs is { } playerPreferences &&
               _ncCharacterIds.TryGetValue(userId, out var characters) &&
               characters.TryGetValue(playerPreferences.SelectedCharacterIndex, out characterId) &&
               characterId.IsValid;
    }

    /// <summary>
    /// Reloads the profile cache after the round snapshot transaction deletes a dead character.
    /// If the account lost its final profile, the normal creation path assigns a fresh one.
    /// </summary>
    public async Task ReloadNCPersistentCharactersAsync(NetUserId userId)
    {
        var stored = await GetOrCreatePreferencesAsync(userId, CancellationToken.None);
        CacheNCCharacterData(userId, stored);

        if (!_cachedPlayerPrefs.TryGetValue(userId, out var cached))
            return;

        cached.Prefs = ConvertPreferences(stored);
        cached.PrefsLoaded = true;
        if (!_playerManager.TryGetSessionById(userId, out var session))
            return;

        _netManager.ServerSendMessage(new MsgPreferencesAndSettings
        {
            Preferences = cached.Prefs,
            Settings = new GameSettings { MaxCharacterSlots = MaxCharacterSlots },
        }, session.Channel);
    }
}
