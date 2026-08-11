// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Agent.Shards;
using Content.Shared._NC.Agent.Shards.Directory;
using Content.Shared.CartridgeLoader;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent.Shards;

/// <summary>
/// Supplies the DIRECTORY shard from persistent Night City employment records.
/// </summary>
public sealed partial class NCAgentDirectorySystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentDirectoryComponent, CartridgeUiReadyEvent>(OnUiReady);
    }

    private void OnUiReady(Entity<NCAgentDirectoryComponent> ent, ref CartridgeUiReadyEvent args)
    {
        var loader = args.Loader.Owner;
        if (!HasComp<NCAgentShardStorageComponent>(loader))
            return;

        // Event handlers cannot await by-ref events, so only immutable IDs/settings cross the async boundary.
        _ = UpdateDirectoryAsync(ent.Owner, loader, ent.Comp.ResultLimit);
    }

    private async Task UpdateDirectoryAsync(EntityUid shard, EntityUid loader, int resultLimit)
    {
        try
        {
            var personnel = await _database.SearchNCPublicPersonnelAsync(string.Empty, resultLimit);
            if (Deleted(shard) || Deleted(loader))
                return;

            var departments = _prototypes.EnumeratePrototypes<DepartmentPrototype>().ToArray();
            var records = personnel.Select(value =>
            {
                var jobId = new ProtoId<JobPrototype>(value.JobPrototypeId);
                var jobTitle = _prototypes.TryIndex(jobId, out var job)
                    ? job.LocalizedName
                    : value.JobPrototypeId;
                var department = departments.FirstOrDefault(value => value.Roles.Contains(jobId));
                var organization = department == null ? "INDEPENDENT" : Loc.GetString(department.Name);
                return new NCAgentDirectoryRecord(value.CharacterId.Value,
                    value.CharacterName,
                    value.JobPrototypeId,
                    jobTitle,
                    organization,
                    value.StartedAt);
            }).ToList();

            _cartridges.UpdateCartridgeUiState(loader, new NCAgentDirectoryUiState(records));
        }
        catch (Exception exception)
        {
            Log.Error($"Agent DIRECTORY failed to read persistent personnel data: {exception}");
            if (!Deleted(loader))
                _cartridges.UpdateCartridgeUiState(loader, new NCAgentDirectoryUiState([]));
        }
    }
}
