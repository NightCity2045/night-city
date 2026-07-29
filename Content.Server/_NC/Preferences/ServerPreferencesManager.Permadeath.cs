// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared.Preferences;
using Robust.Shared.Network;
using Robust.Shared.Player;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

public partial interface IServerPreferencesManager
{
    /// <summary>
    /// Rebuilds the lobby preference cache after a profile was deleted by permadeath.
    /// </summary>
    Task RefreshAfterNCPermadeathAsync(ICommonSession session, int? deletedProfileId);
}

public sealed partial class ServerPreferencesManager
{
    public async Task RefreshAfterNCPermadeathAsync(ICommonSession session, int? deletedProfileId)
    {
        if (_cachedPlayerPrefs.TryGetValue(session.UserId, out var cached))
        {
            var deletedSlot = cached.ProfileIds
                .Where(entry => deletedProfileId == null || entry.Value.Value == deletedProfileId)
                .Select(entry => (int?) entry.Key)
                .FirstOrDefault();
            if (deletedSlot != null && cached.Prefs != null &&
                cached.Prefs.Characters.Count == 1)
            {
                // The lobby UI requires one slot. This creates a new blank identity, never revives the deleted one.
                await _db.SaveCharacterSlotAsync(
                    session.UserId,
                    HumanoidCharacterProfile.Random(),
                    deletedSlot.Value);
            }
        }

        OnClientDisconnected(session);
        await LoadData(session, CancellationToken.None);
        FinishLoad(session);
    }
}
