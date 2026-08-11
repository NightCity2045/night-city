// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.CartridgeLoader;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Agent.Shards;

/// <summary>
/// Enforces the data-driven Agent memory budget on both prediction and server authority.
/// </summary>
public sealed partial class NCAgentShardSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProgramInstallationAttempt>(OnInstallationAttempt);
    }

    private void OnInstallationAttempt(ref ProgramInstallationAttempt args)
    {
        if (!TryComp<NCAgentShardStorageComponent>(args.LoaderUid, out var storage))
            return;

        var requested = GetPrototypeSize(args.Prototype, storage.DefaultProgramSize);
        if (requested <= 0 || GetUsedMemory(args.LoaderUid, storage) + requested > storage.Capacity)
            args.Cancelled = true;
    }

    /// <summary>
    /// Counts only installed programs. A physical chip sitting in the cartridge slot uses no Agent memory yet.
    /// </summary>
    public int GetUsedMemory(EntityUid loader, NCAgentShardStorageComponent? storage = null)
    {
        if (!Resolve(loader, ref storage, false))
            return 0;

        var used = 0;
        foreach (var program in _cartridges.GetDiskPrograms(loader))
            used += GetProgramSize(program, storage.DefaultProgramSize);

        return used;
    }

    public int GetProgramSize(EntityUid program, int fallback)
    {
        return TryComp<NCAgentShardComponent>(program, out var shard)
            ? Math.Max(1, shard.Size)
            : Math.Max(1, fallback);
    }

    private int GetPrototypeSize(string prototypeId, int fallback)
    {
        if (!_prototypes.TryIndex<EntityPrototype>(prototypeId, out var prototype) ||
            !prototype.TryComp<NCAgentShardComponent>(out var shard, EntityManager.ComponentFactory))
        {
            return Math.Max(1, fallback);
        }

        return Math.Max(1, shard.Size);
    }
}
