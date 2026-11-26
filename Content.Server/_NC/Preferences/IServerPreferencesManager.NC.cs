// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;
using Robust.Shared.Network;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

public partial interface IServerPreferencesManager
{
    bool TryGetSelectedNCCharacterId(NetUserId userId, out NCCharacterId characterId);
    Task ReloadNCPersistentCharactersAsync(NetUserId userId);
}
