// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private const string RoundSnapshotSectionType = "player-medical";
    private const string RoundSnapshotOwnerType = "character";
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCRoundRestoreContextSystem _restoreContext = default!;

    private readonly Dictionary<EntityUid, PlayerBinding> _playerBindings = [];
    private readonly Dictionary<EntityUid, PlayerBinding> _disconnectedMedicalBodies = [];
    private readonly Dictionary<NCCharacterId, EntityUid> _boundCharacters = [];
    private readonly Dictionary<NCCharacterId, OwnerSaveState> _saveStates = [];
    private readonly HashSet<EntityUid> _loadingBodies = [];
    private readonly HashSet<EntityUid> _suppressPersistenceEvents = [];
    private readonly Dictionary<NCCharacterId, PendingWrite> _roundSnapshots = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerBeforeSpawnEvent>(OnBorgBeforeSpawn);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCMedicalBodyComponent, EntityTerminatingEvent>(OnMedicalBodyTerminating);
        SubscribeLocalEvent<MapComponent, EntityTerminatingEvent>(OnMapTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart, before: [typeof(SharedMindSystem)]);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnActiveWorldSessionStarted);
        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnRoundSnapshotCapture);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        var body = args.Mob;
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
        {
            return;
        }

        if (TryGetLiveBorgCharacter(characterId, out var liveCore) && liveCore != body)
        {
            if (!TryReclaimBorgCharacter(body, liveCore, characterId))
                MarkOwnerBlocked(body, NCMedicalStableOwnerKind.PlayerCharacter,
                    characterId.Value.ToString(CultureInfo.InvariantCulture), 0);
            return;
        }

        if (_boundCharacters.TryGetValue(characterId, out var existing) &&
            existing != body && Exists(existing))
        {
            Log.Error($"Refused duplicate live medical persistence owner {characterId.Value} for {body}.");
            MarkOwnerBlocked(body, NCMedicalStableOwnerKind.PlayerCharacter,
                characterId.Value.ToString(CultureInfo.InvariantCulture), 0);
            return;
        }

        var state = new OwnerSaveState();
        _saveStates[characterId] = state;
        _boundCharacters[characterId] = body;
        var binding = new PlayerBinding(characterId, state);
        _playerBindings[body] = binding;
        BindOwner(body, NCMedicalStableOwnerKind.PlayerCharacter,
            characterId.Value.ToString(CultureInfo.InvariantCulture), 0);
        _loadingBodies.Add(body);

        NCMedicalStoredSnapshotData? stored;
        try
        {
            var ownerId = characterId.Value.ToString(CultureInfo.InvariantCulture);
            var sections = _restoreContext.Manifest is { } manifest
                ? await _database.GetNCRoundSnapshotSectionsAsync(manifest.SnapshotId,
                    RoundSnapshotSectionType, RoundSnapshotOwnerType, ownerId)
                : await _database.GetLatestNCRoundSnapshotSectionsAsync(
                    RoundSnapshotSectionType, RoundSnapshotOwnerType, ownerId);
            var section = sections is [{ ChunkIndex: 0 }] ? sections[0] : null;
            stored = section == null
                ? await _database.GetNCMedicalSnapshotAsync(characterId)
                : new NCMedicalStoredSnapshotData(
                    characterId,
                    (ushort) section.FormatVersion,
                    1,
                    section.Payload,
                    DateTime.UtcNow,
                    DateTime.UtcNow);
        }
        catch (Exception exception)
        {
            _taskManager.RunOnMainThread(() =>
                FailClosed(body, binding, $"database read failed: {exception}"));
            return;
        }

        _taskManager.RunOnMainThread(() => RestorePlayerMedical(body, args.Player, binding, stored));
    }

    private void RestorePlayerMedical(
        EntityUid body,
        ICommonSession session,
        PlayerBinding binding,
        NCMedicalStoredSnapshotData? stored)
    {
        if (!IsCurrentPlayerBinding(body, session, binding))
        {
            ReleasePlayerBinding(body, binding);
            return;
        }

        if (stored == null)
        {
            _loadingBodies.Remove(body);
            return;
        }

        binding.State.ExpectedRevision = stored.Revision;
        SetOwnerRevision(body, stored.Revision);
        if (TryDecodeStoredSnapshot(stored, out var decoded) && decoded.BorgCore != null)
        {
            if (!TryRestorePlayerBorg(body, stored))
                FailClosed(body, binding, "stored Borg carrier could not be restored");
            return;
        }
        var restored = TryRestoreStoredMedicalState(body, stored);
        _loadingBodies.Remove(body);
        if (!restored)
            FailClosed(body, binding, "stored payload is corrupt or incompatible with the materialized body");
    }

    private void OnPlayerDetached(PlayerDetachedEvent args)
    {
        if (!_playerBindings.TryGetValue(args.Entity, out var binding))
            return;

        // Map deletion and round cleanup can detach the session after termination has begun. The
        // semantic organ containers are still readable at this boundary, so preserve them instead of
        // rejecting an otherwise valid final snapshot and waiting for a later event that may not fire.
        // A test/admin may also detach after deliberately dismantling the graph outside the NC API; keep
        // the last valid persisted revision in that case and report a warning instead of poisoning the
        // shared test/runtime log with an unrecoverable write attempt.
        CapturePlayerMemory(
            args.Entity,
            allowTerminating: TerminatingOrDeleted(args.Entity),
            invalidIsError: false);
        // Disconnection never authorizes a second physical brain. Keep the carrier's stable ownership and
        // checkpoints alive; a later spawn for this character reuses it instead of loading a second copy.
        if (HasComp<NCBorgCoreComponent>(args.Entity) && !TerminatingOrDeleted(args.Entity))
            return;
        ReleasePlayerBinding(args.Entity, binding, keepSaveState: true);
    }

    private void OnMedicalBodyChanged(
        Entity<NCMedicalBodyComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        var body = entity.Owner;
        if (_suppressPersistenceEvents.Contains(body) || _loadingBodies.Contains(body) ||
            !_playerBindings.ContainsKey(body))
        {
            return;
        }

        // Medical state remains authoritative in ECS until the coordinator captures one end-of-round snapshot.
    }

    private void OnMedicalBodyTerminating(
        Entity<NCMedicalBodyComponent> entity,
        ref EntityTerminatingEvent args)
    {
        if (TryComp<NCMedicalPersistenceOwnerComponent>(entity, out var owner) &&
            owner.Kind == NCMedicalStableOwnerKind.PlayerCharacter && int.TryParse(owner.Key, out var number) &&
            _borgCharacters.GetValueOrDefault(new NCCharacterId(number)) == entity.Owner)
            _borgCharacters.Remove(new NCCharacterId(number));
        CapturePlayerMemory(entity.Owner, allowTerminating: true);
        _disconnectedMedicalBodies.Remove(entity.Owner);
        if (_playerBindings.TryGetValue(entity.Owner, out var binding))
            ReleasePlayerBinding(entity.Owner, binding, keepSaveState: true);
    }

    private void OnMapTerminating(Entity<MapComponent> map, ref EntityTerminatingEvent args)
    {
        // Map deletion recursively terminates contained organs before a body's own fallback can serialize
        // them. Bound players are a small indexed set, so capture those on this map while their graph is
        // still intact and release the doomed runtime binding after queuing the write.
        foreach (var (body, binding) in _playerBindings.ToArray())
        {
            if (!Exists(body) || Transform(body).MapUid != map.Owner)
                continue;

            CapturePlayerMemory(body, allowTerminating: TerminatingOrDeleted(body));
            ReleasePlayerBinding(body, binding, keepSaveState: true);
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        // Run before SharedMindSystem detaches players and queues their bodies. A direct restart may skip
        // the normal round-end message, so this is the last guaranteed complete graph boundary.
        foreach (var (body, binding) in _playerBindings.ToArray())
        {
            CapturePlayerMemory(body, allowTerminating: false);
            ReleasePlayerBinding(body, binding, keepSaveState: true);
        }
        _roundSnapshots.Clear();
        _disconnectedMedicalBodies.Clear();
    }

    private void OnActiveWorldSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _roundSnapshots.Clear();
        _disconnectedMedicalBodies.Clear();
    }

    private void OnRoundSnapshotCapture(NCRoundSnapshotCaptureEvent args)
    {
        foreach (var body in _playerBindings.Keys.Concat(_disconnectedMedicalBodies.Keys).Distinct().ToArray())
        {
            if (!CapturePlayerMemory(body, allowTerminating: TerminatingOrDeleted(body)))
                args.Fail($"Medical capture failed for {ToPrettyString(body)}; refusing a mixed ownership checkpoint.");
        }

        foreach (var (characterId, pending) in _roundSnapshots.OrderBy(value => value.Key.Value))
        {
            if (!args.TryAddSection(
                    RoundSnapshotSectionType,
                    RoundSnapshotOwnerType,
                    characterId.Value.ToString(CultureInfo.InvariantCulture),
                    pending.SnapshotVersion,
                    1,
                    pending.Payload))
            {
                return;
            }
        }
    }

    private bool CapturePlayerMemory(
        EntityUid body,
        bool allowTerminating,
        bool invalidIsError = true)
    {
        if (_loadingBodies.Contains(body) ||
            !_playerBindings.TryGetValue(body, out var binding) && !_disconnectedMedicalBodies.TryGetValue(body, out binding))
            return false;

        var snapshot = SerializeMedicalState(body, allowTerminating);
        if (snapshot == null || !TryEncodeSnapshot(snapshot, out var payload))
        {
            var message = $"Refused to capture invalid medical state for character {binding.CharacterId.Value}.";
            if (invalidIsError)
                Log.Error(message);
            else
                Log.Warning(message);
            return false;
        }

        _roundSnapshots[binding.CharacterId] = new PendingWrite(snapshot.Version, payload, DateTime.UtcNow);
        return true;
    }

    /// <summary>
    /// Exposes cheap counters to the F7 monitor without serializing another medical graph.
    /// </summary>
    public NCMedicalPersistenceRuntimeCounters GetRuntimeCounters()
    {
        return new NCMedicalPersistenceRuntimeCounters(
            _playerBindings.Count,
            0,
            _roundSnapshots.Count,
            0,
            _roundSnapshots.Count,
            0,
            0);
    }

    private bool IsCurrentPlayerBinding(EntityUid body, ICommonSession session, PlayerBinding binding)
    {
        return Exists(body) &&
               session.AttachedEntity == body &&
               _playerBindings.GetValueOrDefault(body) == binding &&
               _boundCharacters.GetValueOrDefault(binding.CharacterId) == body &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out var selected) &&
               selected == binding.CharacterId;
    }

    private void FailClosed(EntityUid body, PlayerBinding binding, string reason)
    {
        _loadingBodies.Remove(body);
        binding.State.Blocked = true;
        SetOwnerBlocked(body);
        Log.Error($"Medical persistence for character {binding.CharacterId.Value} failed closed: {reason}.");
    }

    private void ReleasePlayerBinding(EntityUid body, PlayerBinding binding, bool keepSaveState = false)
    {
        // Keep observing the remaining physical body at the common checkpoint after logout. A cached logout
        // payload is no longer authoritative if another character extracts its hardware later in the round.
        if (keepSaveState && !TerminatingOrDeleted(body))
            _disconnectedMedicalBodies[body] = binding;
        _loadingBodies.Remove(body);
        _playerBindings.Remove(body);
        if (_boundCharacters.GetValueOrDefault(binding.CharacterId) == body)
            _boundCharacters.Remove(binding.CharacterId);
        if (!keepSaveState)
            _saveStates.Remove(binding.CharacterId);
    }

    private void BindOwner(
        EntityUid body,
        NCMedicalStableOwnerKind kind,
        string key,
        long revision)
    {
        // Component composition is immutable once EntityTerminating starts. Final checkpoints may read an
        // existing owner marker, but must never create or rewrite one while teardown is in progress.
        if (TerminatingOrDeleted(body))
            return;

        var owner = EnsureComp<NCMedicalPersistenceOwnerComponent>(body);
        owner.Kind = kind;
        owner.Key = key;
        owner.Revision = revision;
        owner.SaveBlocked = false;
    }

    private void MarkOwnerBlocked(
        EntityUid body,
        NCMedicalStableOwnerKind kind,
        string key,
        long revision)
    {
        BindOwner(body, kind, key, revision);
        SetOwnerBlocked(body);
    }

    private void SetOwnerRevision(EntityUid body, long revision)
    {
        if (TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner))
            owner.Revision = revision;
    }

    private void SetOwnerBlocked(EntityUid body)
    {
        if (TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner))
            owner.SaveBlocked = true;
    }

    private bool TryEncodeSnapshot(NCMedicalStateSnapshot snapshot, out string payload)
    {
        payload = string.Empty;
        try
        {
            var node = _serialization.WriteValue(
                snapshot,
                alwaysWrite: true,
                notNullableOverride: true);
            payload = node.ToString();
            return payload.Length is > 0 and <= NCMedicalCharacterSnapshot.MaximumPayloadLength;
        }
        catch (Exception exception)
        {
            Log.Error($"Medical snapshot serialization failed: {exception}");
            return false;
        }
    }

    private bool TryDecodeStoredSnapshot(
        NCMedicalStoredSnapshotData stored,
        out NCMedicalStateSnapshot snapshot)
    {
        snapshot = default!;
        if (stored.Revision <= 0 ||
            stored.Payload.Length is <= 0 or > NCMedicalCharacterSnapshot.MaximumPayloadLength)
        {
            return false;
        }

        try
        {
            using var reader = new StringReader(stored.Payload);
            var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
            if (documents.Length != 1)
                return false;

            snapshot = _serialization.Read<NCMedicalStateSnapshot>(
                documents[0].Root,
                notNullableOverride: true);
            return snapshot != null && snapshot.Version == stored.SnapshotVersion;
        }
        catch (Exception exception)
        {
            // Stored data is an untrusted persistence boundary. A corrupt payload is rejected atomically,
            // but it is not a server-process fault and must not poison the integration test pool.
            Log.Warning($"Medical snapshot deserialization failed and was rejected: {exception.Message}");
            snapshot = default!;
            return false;
        }
    }

    /// <summary>
    /// Transactional storage boundary used by reconnect loading and recovery tooling. Parsing, rebasing and
    /// full graph validation all complete before semantic component writes begin.
    /// </summary>
    public bool TryRestoreStoredMedicalState(EntityUid body, NCMedicalStoredSnapshotData stored)
    {
        if (TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner) &&
            owner.Kind == NCMedicalStableOwnerKind.PlayerCharacter &&
            (!int.TryParse(owner.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var profileId) ||
             profileId != stored.CharacterId.Value))
        {
            return false;
        }

        if (!TryDecodeStoredSnapshot(stored, out var decoded) ||
            !TryRebaseSnapshotForRuntime(decoded, _timing.CurTime, out var rebased))
        {
            return false;
        }

        _suppressPersistenceEvents.Add(body);
        try
        {
            return RestoreMedicalState(body, rebased);
        }
        finally
        {
            _suppressPersistenceEvents.Remove(body);
        }
    }

    /// <summary>
    /// Rebases monotonic timestamps without applying wall-clock elapsed time. Signed rebased timestamps are
    /// intentional: a wound older than the new process uptime still retains its full online age.
    /// </summary>
    public bool TryRebaseSnapshotForRuntime(
        NCMedicalStateSnapshot source,
        TimeSpan runtimeAnchor,
        out NCMedicalStateSnapshot rebased)
    {
        rebased = default!;
        if (source == null ||
            source.Version is not (NCMedicalSnapshotVersions.InitialSemanticGraph or
                NCMedicalSnapshotVersions.StableOrganIdentity or
                NCMedicalSnapshotVersions.LongitudinalWounds or
                NCMedicalSnapshotVersions.CyberwareGraph or
                NCMedicalSnapshotVersions.PharmacologyGraph or
                NCMedicalSnapshotVersions.SystemicSepsis or
                NCMedicalSnapshotVersions.TransplantRejection or
                NCMedicalSnapshotVersions.WoundVisualization or
                NCMedicalSnapshotVersions.CavityFluidLedger or
                NCMedicalSnapshotVersions.SpatialSurgery or
                NCMedicalSnapshotVersions.ModularCyberware or
                NCMedicalSnapshotVersions.BodyPowerBus or
                NCMedicalSnapshotVersions.BorgCore or
                NCMedicalSnapshotVersions.Humanity or
                NCMedicalSnapshotVersions.CyberwareAbilities or
                NCMedicalSnapshotVersions.CyberwareExteriors or
                NCMedicalSnapshotVersions.Current) ||
            runtimeAnchor < TimeSpan.Zero ||
            !HasNonNullSnapshotShape(source))
        {
            return false;
        }

        var result = CloneSnapshot(source);
        if (!TryGetDelta(runtimeAnchor, source.CapturedAt, out var delta))
            return false;

        if (result.Wounds is { } wounds)
        {
            foreach (var wound in wounds.Records.Values)
            {
                if (!TryShift(wound.CreatedAt, delta, out wound.CreatedAt) ||
                    !TryShift(wound.ModifiedAt, delta, out wound.ModifiedAt))
                    return false;
            }

            foreach (var scar in wounds.Scars)
            {
                if (!TryShift(scar.CreatedAt, delta, out scar.CreatedAt))
                    return false;
            }
        }

        if (result.Treatment is { } treatment)
        {
            foreach (var record in treatment.WoundTreatments.Values)
            {
                if ((record.AppliedFlags & NCWoundTreatmentFlags.Pressure) != 0 &&
                    !TryShift(record.PressureAppliedAt, delta, out record.PressureAppliedAt) ||
                    (record.AppliedFlags & NCWoundTreatmentFlags.Bandaged) != 0 &&
                    !TryShift(record.BandagedAt, delta, out record.BandagedAt) ||
                    (record.AppliedFlags & NCWoundTreatmentFlags.Hemostatic) != 0 &&
                    !TryShift(record.HemostaticAppliedAt, delta, out record.HemostaticAppliedAt) ||
                    !TryShift(record.ModifiedAt, delta, out record.ModifiedAt))
                {
                    return false;
                }
            }

            foreach (var tourniquet in treatment.Tourniquets.Values)
            {
                if (!TryShift(tourniquet.AppliedAt, delta, out tourniquet.AppliedAt) ||
                    !TryShift(tourniquet.IschemiaRiskAt, delta, out tourniquet.IschemiaRiskAt) ||
                    !TryShift(tourniquet.CriticalIschemiaAt, delta, out tourniquet.CriticalIschemiaAt) ||
                    !TryShiftOptional(tourniquet.RemovedAt, delta, out tourniquet.RemovedAt) ||
                    !TryShiftOptional(tourniquet.ReboundEndsAt, delta, out tourniquet.ReboundEndsAt))
                {
                    return false;
                }
            }
        }

        foreach (var cyberware in result.CyberwareModules)
        {
            if (!TryShiftOptional(cyberware.EmpDisabledUntil, delta, out cyberware.EmpDisabledUntil))
                return false;
        }

        foreach (var organ in result.Organs.Values)
        {
            if (organ.Cyberware is { } cyberware &&
                !TryShiftOptional(cyberware.EmpDisabledUntil, delta, out cyberware.EmpDisabledUntil))
            {
                return false;
            }
        }

        result.CapturedAt = runtimeAnchor;
        rebased = result;
        return true;
    }

    private static bool TryGetDelta(TimeSpan target, TimeSpan source, out TimeSpan delta)
    {
        try
        {
            delta = target - source;
            return true;
        }
        catch (OverflowException)
        {
            delta = default;
            return false;
        }
    }

    private static bool TryShift(TimeSpan value, TimeSpan delta, out TimeSpan shifted)
    {
        try
        {
            shifted = value + delta;
            return true;
        }
        catch (OverflowException)
        {
            shifted = default;
            return false;
        }
    }

    private static bool TryShiftOptional(TimeSpan value, TimeSpan delta, out TimeSpan shifted)
    {
        if (value == TimeSpan.Zero)
        {
            shifted = TimeSpan.Zero;
            return true;
        }

        return TryShift(value, delta, out shifted);
    }

    private sealed class PlayerBinding(NCCharacterId characterId, OwnerSaveState state)
    {
        public readonly NCCharacterId CharacterId = characterId;
        public readonly OwnerSaveState State = state;
    }

    private sealed class OwnerSaveState
    {
        public long ExpectedRevision;
        public bool Blocked;
    }

    private sealed record PendingWrite(
        int SnapshotVersion,
        string Payload,
        DateTime CapturedUtc);
}

public readonly record struct NCMedicalPersistenceRuntimeCounters(
    int BoundPlayers,
    int DebouncedWrites,
    int QueuedWrites,
    int InFlightWrites,
    long CapturedWrites,
    double LastCaptureMilliseconds,
    double MaximumCaptureMilliseconds);
