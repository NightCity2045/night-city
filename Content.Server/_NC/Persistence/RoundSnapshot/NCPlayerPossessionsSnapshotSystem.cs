// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared.GameTicking;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Robust.Server.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>
/// Captures equipped, held and recursively contained player possessions independently from map and property state.
/// </summary>
public sealed partial class NCPlayerPossessionsSnapshotSystem : EntitySystem
{
    private const string SectionType = "player-possessions";
    private const string OwnerType = "character";
    private const int FormatVersion = 1;
    private const int MaximumRoots = 64;
    private const int MaximumEntities = 512;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<EntityUid, NCCharacterId> _bindings = [];
    private readonly Dictionary<NCCharacterId, PossessionsDocument> _detached = [];
    private bool _capturing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart, before: [typeof(SharedMindSystem)]);
        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnRoundSnapshotCapture);
        _loader.OnIsSerializable += OnIsSerializable;
    }

    public override void Shutdown()
    {
        _loader.OnIsSerializable -= OnIsSerializable;
        base.Shutdown();
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
            return;

        _bindings[args.Mob] = characterId;
        PossessionsDocument? document = null;
        if (_detached.Remove(characterId, out var sameRound))
        {
            document = sameRound;
        }
        else
        {
            try
            {
                var sections = await _database.GetLatestNCRoundSnapshotSectionsAsync(
                    SectionType,
                    OwnerType,
                    Key(characterId));
                if (sections is [{ ChunkIndex: 0, FormatVersion: FormatVersion }])
                    document = JsonSerializer.Deserialize<PossessionsDocument>(sections[0].Payload);
            }
            catch (Exception exception)
            {
                Log.Error($"Failed to load possessions for character {characterId.Value}: {exception}");
                return;
            }
        }

        if (document == null)
            return;

        _taskManager.RunOnMainThread(() => RestoreIfCurrent(args.Mob, args.Player, characterId, document));
    }

    private void RestoreIfCurrent(
        EntityUid body,
        ICommonSession session,
        NCCharacterId characterId,
        PossessionsDocument document)
    {
        if (!_bindings.TryGetValue(body, out var current) || current != characterId ||
            session.AttachedEntity != body)
        {
            return;
        }

        if (!TryRestore(body, document, out var error))
            Log.Error($"Failed to restore possessions for character {characterId.Value}: {error}");
    }

    private void OnPlayerDetached(PlayerDetachedEvent args)
    {
        if (!_bindings.Remove(args.Entity, out var characterId))
            return;
        if (TryCapture(args.Entity, out var document, out var error))
            _detached[characterId] = document;
        else
            Log.Warning($"Could not retain disconnected character {characterId.Value} possessions: {error}");
    }

    private void OnRoundSnapshotCapture(NCRoundSnapshotCaptureEvent args)
    {
        foreach (var (body, characterId) in _bindings.ToArray())
        {
            if (!Exists(body))
                continue;
            if (!TryCapture(body, out var document, out var error))
            {
                args.Fail($"Character {characterId.Value} possessions failed: {error}");
                continue;
            }
            _detached[characterId] = document;
        }

        foreach (var (characterId, document) in _detached.OrderBy(value => value.Key.Value))
        {
            args.TryAddSection(
                SectionType,
                OwnerType,
                Key(characterId),
                FormatVersion,
                document.EntityCount,
                JsonSerializer.Serialize(document));
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _bindings.Clear();
        _detached.Clear();
    }

    private bool TryCapture(EntityUid body, out PossessionsDocument document, out string error)
    {
        document = default!;
        error = string.Empty;
        if (!Exists(body))
        {
            error = "body no longer exists";
            return false;
        }

        var roots = new List<Placement>();
        var seen = new HashSet<EntityUid>();
        var slots = _inventory.GetSlotEnumerator(body);
        while (slots.NextItem(out var item, out var slot))
        {
            if (slot == null || !seen.Add(item))
                continue;
            roots.Add(new Placement(PossessionPlacement.Slot, slot.Name, item));
        }

        if (TryComp<HandsComponent>(body, out var hands))
        {
            foreach (var handId in _hands.EnumerateHands((body, hands)))
            {
                if (_hands.TryGetHeldItem((body, hands), handId, out var item) && seen.Add(item.Value))
                    roots.Add(new Placement(PossessionPlacement.Hand, handId, item.Value));
            }
        }

        if (roots.Count > MaximumRoots)
        {
            error = $"root limit {MaximumRoots} was exceeded";
            return false;
        }

        var entries = new List<PossessionEntry>(roots.Count);
        var entityCount = 0;
        _capturing = true;
        try
        {
            foreach (var placement in roots)
            {
                entityCount = checked(entityCount + CountTree(placement.Entity));
                if (entityCount > MaximumEntities)
                {
                    error = $"entity limit {MaximumEntities} was exceeded";
                    return false;
                }

                using var writer = new StringWriter();
                var options = SerializationOptions.Default with
                {
                    MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                    ErrorOnOrphan = false,
                    LogAutoInclude = null,
                };
                if (!_loader.TrySaveGeneric([placement.Entity], writer, out _, options))
                {
                    error = $"could not serialize {placement.Kind} placement '{placement.Id}'";
                    return false;
                }
                entries.Add(new PossessionEntry(placement.Kind, placement.Id, writer.ToString()));
            }
        }
        finally
        {
            _capturing = false;
        }

        document = new PossessionsDocument(FormatVersion, entityCount, entries);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document)) > MaximumPayloadBytes)
        {
            error = $"payload limit {MaximumPayloadBytes} bytes was exceeded";
            return false;
        }
        return true;
    }

    private bool TryRestore(EntityUid body, PossessionsDocument document, out string error)
    {
        error = string.Empty;
        if (document.Version != FormatVersion || document.EntityCount is < 0 or > MaximumEntities ||
            document.Entries.Count > MaximumRoots)
        {
            error = "document bounds or version are invalid";
            return false;
        }

        var loaded = new List<(PossessionEntry Entry, EntityUid Root)>(document.Entries.Count);
        foreach (var entry in document.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.EntityYaml) ||
                !Enum.IsDefined(entry.Kind))
            {
                DeleteLoaded(loaded);
                error = "a placement record is invalid";
                return false;
            }

            using var reader = new StringReader(entry.EntityYaml);
            if (!_loader.TryLoadGeneric(reader, $"character possessions {entry.Kind}/{entry.Id}", out var result,
                    MapLoadOptions.Default) || result.Maps.Count != 0 || result.Grids.Count != 0 ||
                result.Orphans.Count != 1 || result.RootNodes.Count != 1)
            {
                foreach (var root in result?.RootNodes ?? [])
                {
                    if (Exists(root))
                        Del(root);
                }
                DeleteLoaded(loaded);
                error = $"entity graph for {entry.Kind} placement '{entry.Id}' is invalid";
                return false;
            }
            loaded.Add((entry, result.Orphans.Single()));
        }

        DeleteCurrentPossessions(body);
        foreach (var (entry, root) in loaded)
        {
            _transform.SetCoordinates(root, Transform(body).Coordinates);
            var placed = entry.Kind switch
            {
                PossessionPlacement.Slot => _inventory.TryEquip(body, root, entry.Id, silent: true, force: true),
                PossessionPlacement.Hand => TryPlaceInHand(body, root, entry.Id),
                _ => false,
            };
            if (placed)
                continue;

            DeleteLoaded(loaded);
            error = $"could not place restored entity into {entry.Kind} '{entry.Id}'";
            return false;
        }
        return true;
    }

    private bool TryPlaceInHand(EntityUid body, EntityUid item, string handId)
    {
        if (!TryComp<HandsComponent>(body, out var hands) || !_hands.HandIsEmpty((body, hands), handId))
            return false;
        _hands.DoPickup(body, handId, item, hands, log: false);
        return _hands.IsHolding((body, hands), item);
    }

    private void DeleteCurrentPossessions(EntityUid body)
    {
        var roots = new HashSet<EntityUid>();
        var slots = _inventory.GetSlotEnumerator(body);
        while (slots.NextItem(out var item))
            roots.Add(item);
        if (TryComp<HandsComponent>(body, out var hands))
        {
            foreach (var item in _hands.EnumerateHeld((body, hands)))
                roots.Add(item);
        }
        foreach (var root in roots)
        {
            if (Exists(root))
                Del(root);
        }
    }

    private void DeleteLoaded(IEnumerable<(PossessionEntry Entry, EntityUid Root)> loaded)
    {
        foreach (var (_, root) in loaded)
        {
            if (Exists(root))
                Del(root);
        }
    }

    private int CountTree(EntityUid root)
    {
        var count = 0;
        var pending = new Stack<EntityUid>();
        var seen = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var entity))
        {
            if (!seen.Add(entity))
                continue;
            count++;
            var children = Transform(entity).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Push(child);
        }
        return count;
    }

    private void OnIsSerializable(Entity<MetaDataComponent> entity, ref bool serializable)
    {
        if (!_capturing || !serializable)
            return;
        if (HasComp<ActorComponent>(entity.Owner))
            serializable = false;
    }

    private static string Key(NCCharacterId characterId) =>
        characterId.Value.ToString(CultureInfo.InvariantCulture);

    private enum PossessionPlacement : byte
    {
        Slot,
        Hand,
    }

    private readonly record struct Placement(PossessionPlacement Kind, string Id, EntityUid Entity);
    private sealed record PossessionsDocument(int Version, int EntityCount, List<PossessionEntry> Entries);
    private sealed record PossessionEntry(PossessionPlacement Kind, string Id, string EntityYaml);
}
