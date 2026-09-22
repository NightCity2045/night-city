// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using AstraGraph.Runtime.Security;
using Content.Server.Administration.Managers;
using Content.Shared._NC.AstraGraph;
using Content.Shared.Administration;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Reads admin flags from <see cref="IAdminManager"/>. AstraGraph does not reflect over the session.
/// </summary>
public sealed class NightCityAstraAdminDirectory : IAstraAdminDirectory
{
    private readonly IAdminManager _admins;
    private readonly ISharedPlayerManager _players;

    public NightCityAstraAdminDirectory(IAdminManager admins, ISharedPlayerManager players)
    {
        _admins = admins;
        _players = players;
    }

    public bool TryGetAdmin(string userId, out uint adminFlags, out string? rank, out bool isSandbox)
    {
        adminFlags = 0;
        rank = null;
        isSandbox = false;

        if (!Guid.TryParse(userId, out var parsed))
            return false;

        if (!_players.TryGetSessionById(new NetUserId(parsed), out var session))
            return false;

        var data = _admins.GetAdminData(session);
        if (data == null || !data.Active)
            return false;

        adminFlags = (uint)data.Flags;
        rank = AstraAdminRankMap.Map(data.Title, data.HasFlag(AdminFlags.Host));
        return true;
    }
}
