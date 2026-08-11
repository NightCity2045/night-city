// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Agent.Shards;
using Content.Shared._NC.Agent.Shards.Watchlist;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database._NC.Police;

namespace Content.Server._NC.Agent.Shards;

/// <summary>
/// Supplies the protected WATCHLIST shard from persistent NCPD records.
/// </summary>
public sealed partial class NCAgentWatchlistSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private IServerDbManager _database = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentWatchlistComponent, CartridgeUiReadyEvent>(OnUiReady);
    }

    private void OnUiReady(Entity<NCAgentWatchlistComponent> ent, ref CartridgeUiReadyEvent args)
    {
        var loader = args.Loader.Owner;
        if (!HasComp<NCAgentShardStorageComponent>(loader))
            return;

        // Event handlers cannot await by-ref events, so only immutable IDs/settings cross the async boundary.
        _ = UpdateWatchlistAsync(ent.Owner, loader, ent.Comp.ResultLimit, ent.Comp.WarrantLimit);
    }

    private async Task UpdateWatchlistAsync(EntityUid shard, EntityUid loader, int resultLimit, int warrantLimit)
    {
        try
        {
            var records = (await _database.SearchNCPoliceRecordsAsync(string.Empty, resultLimit))
                .Where(value => value.Status != NCPoliceOperationalStatus.None)
                .Select(value => new NCAgentWatchlistRecord(
                    value.CharacterId.Value,
                    value.CharacterName,
                    value.JobPrototypeId,
                    value.Status,
                    value.Reason,
                    value.UpdatedByName,
                    value.UpdatedAt))
                .ToList();

            var warrants = (await _database.GetNCPoliceWarrantsAsync(warrantLimit))
                .Select(value => new NCAgentWatchlistWarrant(
                    value.Id,
                    value.CaseId,
                    value.TargetCharacterId.Value,
                    value.TargetName,
                    value.Type,
                    value.Status,
                    value.Reason,
                    value.IssuedByName,
                    value.IssuedAt,
                    value.ResolvedByName,
                    value.ResolutionReason,
                    value.ResolvedAt))
                .ToList();
            if (!Deleted(shard) && !Deleted(loader))
                _cartridges.UpdateCartridgeUiState(loader, new NCAgentWatchlistUiState(records, warrants));
        }
        catch (Exception exception)
        {
            Log.Error($"Agent WATCHLIST failed to read persistent NCPD data: {exception}");
            if (!Deleted(loader))
                _cartridges.UpdateCartridgeUiState(loader, new NCAgentWatchlistUiState([], []));
        }
    }
}
