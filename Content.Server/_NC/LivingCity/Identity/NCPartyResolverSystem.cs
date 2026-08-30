// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using System.Threading.Tasks;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Resolves every persistent party without consulting ECS bindings. Materialization is irrelevant to legal identity.
/// </summary>
public sealed partial class NCPartyResolverSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;

    public async Task<NCPartyResolution> ResolveAsync(NCPartyRef party, bool requireActive = false)
    {
        if (!party.IsValid)
            return Reject(party, NCPartyResolutionStatus.Invalid, "The party reference is invalid.");

        NCPartyResolution resolution;
        switch (party.Kind)
        {
            case NCPartyKind.Character:
            {
                var character = await _database.GetNCCharacterPartyProjectionAsync(party.CharacterId);
                resolution = character == null
                    ? Reject(party, NCPartyResolutionStatus.NotFound, "The character party does not exist.")
                    : new NCPartyResolution(
                        NCPartyResolutionStatus.Resolved,
                        party,
                        character.DisplayName,
                        NCPartyLifecycle.Active,
                        0,
                        null);
                break;
            }
            case NCPartyKind.Actor:
            {
                var actor = await _actors.GetAsync(new NCActorId(party.PersistentId));
                if (actor == null)
                {
                    resolution = Reject(party, NCPartyResolutionStatus.NotFound,
                        "The persistent actor party does not exist.");
                    break;
                }

                var lifecycle = actor.Snapshot.Lifecycle.Status switch
                {
                    NCCityActorStatus.Dead => NCPartyLifecycle.Deceased,
                    NCCityActorStatus.Archived => NCPartyLifecycle.Archived,
                    _ => NCPartyLifecycle.Active,
                };
                resolution = new NCPartyResolution(
                    NCPartyResolutionStatus.Resolved,
                    party,
                    actor.Snapshot.Identity.FullName,
                    lifecycle,
                    actor.AggregateVersion,
                    null);
                break;
            }
            default:
            {
                var record = await _directory.GetAsync(party);
                resolution = record == null
                    ? Reject(party, NCPartyResolutionStatus.NotFound, "The directory party does not exist.")
                    : new NCPartyResolution(
                        NCPartyResolutionStatus.Resolved,
                        party,
                        record.Snapshot.DisplayName,
                        record.Snapshot.Lifecycle,
                        record.AggregateVersion,
                        null);
                break;
            }
        }

        return requireActive && resolution.IsResolved && resolution.Lifecycle != NCPartyLifecycle.Active
            ? resolution with
            {
                Status = NCPartyResolutionStatus.Inactive,
                Error = "The party is not active.",
            }
            : resolution;
    }

    private static NCPartyResolution Reject(NCPartyRef party, NCPartyResolutionStatus status, string error)
    {
        return new NCPartyResolution(status, party, string.Empty, NCPartyLifecycle.Archived, 0, error);
    }
}
