// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Persistence.Identity;
using System.Threading.Tasks;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>Result of resolving a party reference. Only characters resolve; other party kinds were removed.</summary>
public sealed record NCPartyResolution(NCPartyRef Party, string DisplayName, string? Error)
{
    public bool IsResolved => Error == null;
}

/// <summary>
/// Resolves a party reference to a stored character without consulting ECS bindings.
/// </summary>
public sealed partial class NCPartyResolverSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;

    public async Task<NCPartyResolution> ResolveAsync(NCPartyRef party, bool requireActive = false)
    {
        if (!party.IsValid)
            return new NCPartyResolution(party, string.Empty, "The party reference is invalid.");

        // NC: Organizations, actors and the party directory were removed; only characters remain.
        if (party.Kind != NCPartyKind.Character)
            return new NCPartyResolution(party, string.Empty, "Only character parties resolve.");

        var character = await _database.GetNCCharacterPartyProjectionAsync(party.CharacterId);
        return character == null
            ? new NCPartyResolution(party, string.Empty, "The character party does not exist.")
            : new NCPartyResolution(party, character.DisplayName, null);
    }
}
