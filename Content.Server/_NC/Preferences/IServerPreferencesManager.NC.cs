// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using System.Threading.Tasks;

namespace Content.Server.Preferences.Managers;

public partial interface IServerPreferencesManager
{
    bool TryGetSelectedNCCharacterId(NetUserId userId, out NCCharacterId characterId);
    bool TryGetSelectedNCEmployment(NetUserId userId, out ProtoId<JobPrototype> job);
    Task<bool> SetSelectedNCEmploymentAsync(NetUserId userId, ProtoId<JobPrototype>? job);
    Task RefreshNCEmploymentAsync(NetUserId userId);
}
