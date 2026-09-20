// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Text.Json;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.GameTicking;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>
/// Decides character survival at the common capture boundary and refreshes preference caches after commit.
/// The medical system owns death rules; this system only consumes its irreversible result.
/// </summary>
public sealed partial class NCCharacterLifecycleSnapshotSystem : EntitySystem
{
    private const string SectionType = "character-lifecycle";
    private const string OwnerType = "character";
    private const int FormatVersion = 1;

    [Dependency] private IServerPreferencesManager _preferences = default!;

    private readonly Dictionary<EntityUid, CharacterBinding> _bindings = [];
    private readonly Dictionary<NCCharacterId, NetUserId> _owners = [];
    private readonly HashSet<NCCharacterId> _irreversiblyDead = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NCPhysiologyComponent, EntityTerminatingEvent>(OnBodyTerminating);
        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnRoundSnapshotCapture);
        SubscribeLocalEvent<NCRoundSnapshotCommittedEvent>(OnRoundSnapshotCommitted);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
            return;

        _bindings[args.Mob] = new CharacterBinding(characterId, args.Player.UserId);
        _owners[characterId] = args.Player.UserId;
        _irreversiblyDead.Remove(characterId);
    }

    private void OnPlayerDetached(PlayerDetachedEvent args)
    {
        if (!_bindings.Remove(args.Entity, out var binding))
            return;

        RememberDeath(args.Entity, binding.CharacterId);
    }

    private void OnBodyTerminating(Entity<NCPhysiologyComponent> entity, ref EntityTerminatingEvent args)
    {
        if (_bindings.TryGetValue(entity.Owner, out var binding) && entity.Comp.Irreversible)
            _irreversiblyDead.Add(binding.CharacterId);
    }

    private void OnRoundSnapshotCapture(NCRoundSnapshotCaptureEvent args)
    {
        foreach (var (body, binding) in _bindings)
            RememberDeath(body, binding.CharacterId);

        foreach (var (characterId, _) in _owners)
        {
            var deleted = _irreversiblyDead.Contains(characterId);
            var payload = JsonSerializer.Serialize(new CharacterLifecycleDocument(deleted));
            args.TryAddSection(
                SectionType,
                OwnerType,
                characterId.Value.ToString(CultureInfo.InvariantCulture),
                FormatVersion,
                1,
                payload);
            if (deleted)
                args.DeleteCharacter(characterId.Value);
        }
    }

    private void OnRoundSnapshotCommitted(NCRoundSnapshotCommittedEvent args)
    {
        var users = args.DeletedCharacterIds
            .Select(value => new NCCharacterId(value))
            .Where(_owners.ContainsKey)
            .Select(value => _owners[value])
            .Distinct()
            .ToArray();
        if (users.Length != 0)
            args.AddPending(ReloadUsersAsync(users));
    }

    private async Task ReloadUsersAsync(IEnumerable<NetUserId> users)
    {
        foreach (var user in users)
            await _preferences.ReloadNCPersistentCharactersAsync(user);
    }

    private void RememberDeath(EntityUid body, NCCharacterId characterId)
    {
        if (TryComp<NCPhysiologyComponent>(body, out var physiology) && physiology.Irreversible)
            _irreversiblyDead.Add(characterId);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _bindings.Clear();
        _owners.Clear();
        _irreversiblyDead.Clear();
    }

    private readonly record struct CharacterBinding(NCCharacterId CharacterId, NetUserId UserId);
    private sealed record CharacterLifecycleDocument(bool IrreversiblyDead);
}
