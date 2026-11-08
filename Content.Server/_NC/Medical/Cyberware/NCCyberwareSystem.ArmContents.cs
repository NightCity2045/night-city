// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    [Dependency] private MapLoaderSystem _armLoader = default!;
    // Only the old graph of this synchronous medical replacement may share saved physical identities.
    private HashSet<EntityUid> _armContentsReplacedEntities = [];

    private void RegisterReplacedArmContents(EntityUid module)
    {
        var pending = new Stack<EntityUid>();
        var children = Transform(module).ChildEnumerator;
        while (children.MoveNext(out var child)) pending.Push(child);
        while (pending.TryPop(out var uid))
        {
            if (!_armContentsReplacedEntities.Add(uid)) continue;
            var nested = Transform(uid).ChildEnumerator;
            while (nested.MoveNext(out var child)) pending.Push(child);
        }
    }

    private List<NCArmContentsSnapshot> CaptureArmContents(EntityUid module)
    {
        var result = new List<NCArmContentsSnapshot>();
        if (!HasComp<NCArmModuleComponent>(module)) return result;
        if (TryComp<NCCyberwareAbilityComponent>(module, out var ability) && ability.IntegralItem is { } integral && Exists(integral)) Save("integral", integral);
        if (_containers.TryGetContainer(module, "storagebase", out var storage))
            foreach (var uid in storage.ContainedEntities) Save("storage", uid);
        return result;

        void Save(string slot, EntityUid uid)
        {
            using var writer = new StringWriter();
            if (!_armLoader.TrySaveGeneric([uid], writer, out _, SerializationOptions.Default with
                { MissingEntityBehaviour = MissingEntityBehaviour.Ignore, ErrorOnOrphan = false, LogAutoInclude = null }))
                throw new InvalidOperationException("Unable to capture physical cyberarm contents.");
            var yaml = writer.ToString();
            if (yaml.Length > 262144 || result.Count >= 8) throw new InvalidOperationException("Cyberarm contents exceed persistence limits.");
            result.Add(new NCArmContentsSnapshot { Slot = slot, Yaml = yaml });
        }
    }

    private bool RestoreArmContents(EntityUid module, List<NCArmContentsSnapshot> records)
    {
        if (!HasComp<NCArmModuleComponent>(module)) return records.Count == 0;
        var loaded = new List<(string Slot, EntityUid Root)>();
        foreach (var record in records)
        {
            using var reader = new StringReader(record.Yaml);
            if (!_armLoader.TryLoadGeneric(reader, "cyberarm contents", out var data, MapLoadOptions.Default) ||
                data.Maps.Count != 0 || data.Grids.Count != 0 || data.RootNodes.Count != 1 || data.Orphans.Count != 1)
            {
                foreach (var root in data?.RootNodes ?? []) if (Exists(root)) Del(root);
                Cleanup(); return false;
            }
            var rootUid = data.Orphans.Single();
            loaded.Add((record.Slot, rootUid));
            if (!CanUseRestoredWorldTree(rootUid)) { Cleanup(); return false; }
            if (record.Slot == "integral")
            {
                if (!TryComp<NCCyberwareAbilityComponent>(module, out var ability) ||
                    _prototypes.Index(ability.Profile).IntegralItem is not { } expected ||
                    MetaData(rootUid).EntityPrototype?.ID != expected.Id) { Cleanup(); return false; }
            }
            else if (!_containers.TryGetContainer(module, "storagebase", out _)) { Cleanup(); return false; }
        }
        // Validate every new tree before replacing any existing contents.
        if (TryComp<NCCyberwareAbilityComponent>(module, out var current))
        {
            if (current.IntegralItem is { } old && Exists(old)) Del(old);
            current.IntegralItem = null;
        }
        if (_containers.TryGetContainer(module, "storagebase", out var storage))
            foreach (var old in storage.ContainedEntities.ToArray()) Del(old);
        foreach (var (slot, root) in loaded)
        {
            if (slot == "integral")
            {
                var ability = Comp<NCCyberwareAbilityComponent>(module);
                ability.ItemContainer ??= _containers.EnsureContainer<ContainerSlot>(module, NCCyberwareAbilityComponent.ContainerId);
                ability.IntegralItem = root;
                EnsureComp<Abilities.NCIntegralCyberwareItemComponent>(root).Module = module;
                RemComp<Content.Shared.Interaction.Components.UnremoveableComponent>(root);
                if (!_containers.Insert(root, ability.ItemContainer)) { Cleanup(); return false; }
            }
            else if (!_containers.Insert(root, storage!)) { Cleanup(); return false; }
        }
        return true;
        void Cleanup() { foreach (var (_, root) in loaded) if (Exists(root)) Del(root); }
    }
}
