using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Identity;
using Robust.Shared.Network;

namespace Content.Server.Preferences.Managers;

public sealed partial class ServerPreferencesManager
{
    /// <summary>
    /// Resolves a character slot to its stable database identifier without exposing that identifier to the client.
    /// </summary>
    public bool TryGetProfileId(NetUserId userId, int slot, out ProfileId profileId)
    {
        profileId = default;

        return _cachedPlayerPrefs.TryGetValue(userId, out var preferences)
            && preferences.PrefsLoaded
            && preferences.ProfileIds.TryGetValue(slot, out profileId)
            && profileId.IsValid;
    }

    /// <summary>
    /// Resolves the currently selected character to its stable database identifier.
    /// </summary>
    public bool TryGetSelectedProfileId(NetUserId userId, out ProfileId profileId)
    {
        profileId = default;

        return _cachedPlayerPrefs.TryGetValue(userId, out var preferences)
            && preferences.PrefsLoaded
            && preferences.Prefs is { } playerPreferences
            && preferences.ProfileIds.TryGetValue(playerPreferences.SelectedCharacterIndex, out profileId)
            && profileId.IsValid;
    }

    /// <summary>
    /// Builds the server-only slot mapping while complete EF profile records are available.
    /// </summary>
    private static Dictionary<int, ProfileId> CreateProfileIdMap(Preference preferences)
    {
        var result = new Dictionary<int, ProfileId>(preferences.Profiles.Count);

        foreach (var profile in preferences.Profiles)
        {
            var profileId = new ProfileId(profile.Id);
            if (profileId.IsValid)
                result[profile.Slot] = profileId;
        }

        return result;
    }

    /// <summary>
    /// Refreshes identifiers after saving a newly created character slot.
    /// Existing profile edits keep their original database identifier.
    /// </summary>
    private async Task RefreshProfileIdCache(NetUserId userId, PlayerPrefData preferences)
    {
        var storedPreferences = await _db.GetPlayerPreferencesAsync(userId, CancellationToken.None);
        if (storedPreferences != null)
            preferences.ProfileIds = CreateProfileIdMap(storedPreferences);
    }
}
