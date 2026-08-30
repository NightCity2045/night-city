// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Gibbing.Systems;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SaveMaximumDeferral = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(5);

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private ISerializationManager _serialization = default!;

    private readonly Dictionary<EntityUid, PlayerBinding> _playerBindings = [];
    private readonly Dictionary<NCCharacterId, EntityUid> _boundCharacters = [];
    private readonly Dictionary<NCCharacterId, OwnerSaveState> _saveStates = [];
    private readonly Dictionary<EntityUid, TimeSpan> _saveDue = [];
    private readonly Dictionary<EntityUid, TimeSpan> _saveDirtySince = [];
    private readonly Dictionary<NCCharacterId, TimeSpan> _retryDue = [];
    private readonly HashSet<EntityUid> _loadingBodies = [];
    private readonly HashSet<EntityUid> _suppressPersistenceEvents = [];
    private readonly List<EntityUid> _duePlayerWrites = [];
    private readonly List<NCCharacterId> _dueRetries = [];
    private long _capturedPlayerWrites;
    private double _lastCaptureMilliseconds;
    private double _maximumCaptureMilliseconds;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCMedicalBodyComponent, EntityTerminatingEvent>(OnMedicalBodyTerminating);
        SubscribeLocalEvent<MapComponent, EntityTerminatingEvent>(OnMapTerminating);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd, before: [typeof(GibOnRoundEndSystem)]);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart, before: [typeof(SharedMindSystem)]);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        // Reuse bounded scratch lists. Damage schedules a save, so allocating two LINQ pipelines on every
        // server tick while that save is pending creates avoidable GC pressure exactly during combat.
        _duePlayerWrites.Clear();
        foreach (var (body, due) in _saveDue)
        {
            if (due <= now)
                _duePlayerWrites.Add(body);
        }
        foreach (var body in _duePlayerWrites)
        {
            _saveDue.Remove(body);
            _saveDirtySince.Remove(body);
            CapturePlayerWrite(body, allowTerminating: false);
        }

        _dueRetries.Clear();
        foreach (var (characterId, due) in _retryDue)
        {
            if (due <= now)
                _dueRetries.Add(characterId);
        }
        foreach (var characterId in _dueRetries)
        {
            _retryDue.Remove(characterId);
            if (_saveStates.TryGetValue(characterId, out var state))
                StartNextWrite(characterId, state);
        }
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        var body = args.Mob;
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
        {
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
            stored = await _database.GetNCMedicalSnapshotAsync(characterId);
        }
        catch (Exception exception)
        {
            FailClosed(body, binding, $"database read failed: {exception}");
            return;
        }

        if (!IsCurrentPlayerBinding(body, args.Player, binding))
        {
            ReleasePlayerBinding(body, binding);
            return;
        }

        if (stored == null)
        {
            _loadingBodies.Remove(body);
            CapturePlayerWrite(body, allowTerminating: false);
            return;
        }

        state.ExpectedRevision = stored.Revision;
        SetOwnerRevision(body, stored.Revision);
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
        ForceCapturePlayerWrite(
            args.Entity,
            allowTerminating: TerminatingOrDeleted(args.Entity),
            invalidIsError: false);
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

        // Anatomy transitions include detach/reattach/transplant boundaries and must not sit in debounce.
        if ((args.Domains & NCMedicalDirtyDomains.Anatomy) != 0)
        {
            ForceCapturePlayerWrite(body, allowTerminating: false);
            return;
        }

        QueueDebouncedSave(body, _timing.CurTime);
    }

    private void QueueDebouncedSave(EntityUid body, TimeSpan now)
    {
        // Combat damage is a burst, not a request to serialize the complete medical graph every two seconds.
        // Use a trailing edge while retaining a maximum durability bound for long-running bleeding or treatment.
        if (!_saveDirtySince.TryGetValue(body, out var dirtySince))
        {
            dirtySince = now;
            _saveDirtySince[body] = dirtySince;
        }
        _saveDue[body] = Min(now + SaveDebounce, dirtySince + SaveMaximumDeferral);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private void OnMedicalBodyTerminating(
        Entity<NCMedicalBodyComponent> entity,
        ref EntityTerminatingEvent args)
    {
        ForceCapturePlayerWrite(entity.Owner, allowTerminating: true);
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

            ForceCapturePlayerWrite(body, allowTerminating: TerminatingOrDeleted(body));
            ReleasePlayerBinding(body, binding, keepSaveState: true);
        }
    }

    private void OnRoundEnd(RoundEndMessageEvent args)
    {
        ForceCaptureAllPlayers();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        // Run before SharedMindSystem detaches players and queues their bodies. A direct restart may skip
        // the normal round-end message, so this is the last guaranteed complete graph boundary.
        foreach (var (body, binding) in _playerBindings.ToArray())
        {
            ForceCapturePlayerWrite(body, allowTerminating: false);
            ReleasePlayerBinding(body, binding, keepSaveState: true);
        }
    }

    private void ForceCaptureAllPlayers()
    {
        foreach (var body in _playerBindings.Keys.ToArray())
            ForceCapturePlayerWrite(body, allowTerminating: TerminatingOrDeleted(body));
    }

    private void ForceCapturePlayerWrite(
        EntityUid body,
        bool allowTerminating,
        bool invalidIsError = true)
    {
        _saveDue.Remove(body);
        _saveDirtySince.Remove(body);
        CapturePlayerWrite(body, allowTerminating, invalidIsError);
    }

    private void CapturePlayerWrite(
        EntityUid body,
        bool allowTerminating,
        bool invalidIsError = true)
    {
        if (_loadingBodies.Contains(body) ||
            !_playerBindings.TryGetValue(body, out var binding) ||
            binding.State.Blocked)
        {
            return;
        }

        var captureStarted = Stopwatch.GetTimestamp();
        var snapshot = SerializeMedicalState(body, allowTerminating);
        var payload = string.Empty;
        var encoded = snapshot != null && TryEncodeSnapshot(snapshot, out payload);
        var captureMilliseconds = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
        _capturedPlayerWrites++;
        _lastCaptureMilliseconds = captureMilliseconds;
        _maximumCaptureMilliseconds = Math.Max(_maximumCaptureMilliseconds, captureMilliseconds);
        if (!encoded || snapshot == null)
        {
            var message = $"Refused to persist invalid medical state for character {binding.CharacterId.Value}.";
            if (invalidIsError)
                Log.Error(message);
            else
                Log.Warning(message);
            return;
        }

        var capturedUtc = DateTime.UtcNow;
        binding.State.Queued = new PendingWrite(
            snapshot.Version,
            payload,
            capturedUtc);
        StartNextWrite(binding.CharacterId, binding.State);
    }

    /// <summary>
    /// Exposes cheap counters to the F7 monitor without serializing another medical graph.
    /// </summary>
    public NCMedicalPersistenceRuntimeCounters GetRuntimeCounters()
    {
        var queuedWrites = 0;
        var inFlightWrites = 0;
        foreach (var state in _saveStates.Values)
        {
            if (state.Queued != null)
                queuedWrites++;
            if (state.InFlight)
                inFlightWrites++;
        }

        return new NCMedicalPersistenceRuntimeCounters(
            _playerBindings.Count,
            _saveDue.Count,
            queuedWrites,
            inFlightWrites,
            _capturedPlayerWrites,
            _lastCaptureMilliseconds,
            _maximumCaptureMilliseconds);
    }

    private void StartNextWrite(NCCharacterId characterId, OwnerSaveState state)
    {
        if (state.InFlight || state.Blocked || state.Queued is not { } pending)
            return;

        state.Queued = null;
        state.InFlight = true;
        _ = PersistPlayerWriteAsync(characterId, state, pending);
    }

    private async Task PersistPlayerWriteAsync(
        NCCharacterId characterId,
        OwnerSaveState state,
        PendingWrite pending)
    {
        try
        {
            var expectedRevision = state.ExpectedRevision;
            var result = await _database.TrySaveNCMedicalSnapshotAsync(
                new NCMedicalStoredSnapshotData(
                    characterId,
                    pending.SnapshotVersion,
                    expectedRevision,
                    pending.Payload,
                    pending.CapturedUtc,
                    pending.CapturedUtc),
                expectedRevision);
            if (result.Status == NCMedicalSnapshotWriteStatus.Saved)
            {
                state.ExpectedRevision = result.Revision;
                if (_boundCharacters.TryGetValue(characterId, out var body) && Exists(body))
                    SetOwnerRevision(body, result.Revision);
            }
            else
            {
                state.Blocked = true;
                if (_boundCharacters.TryGetValue(characterId, out var body) && Exists(body))
                    SetOwnerBlocked(body);
                Log.Error($"Medical save for character {characterId.Value} failed closed: " +
                          $"{result.Status}, current revision {result.Revision}.");
            }
        }
        catch (Exception exception)
        {
            // Keep the newest captured write and retry without re-reading an entity that may be terminating.
            state.Queued ??= pending;
            _retryDue[characterId] = _timing.CurTime + SaveRetryDelay;
            Log.Error($"Medical save for character {characterId.Value} will retry: {exception}");
        }
        finally
        {
            state.InFlight = false;
        }

        if (!_retryDue.ContainsKey(characterId))
            StartNextWrite(characterId, state);
        if (!state.InFlight && state.Queued == null && !_boundCharacters.ContainsKey(characterId))
        {
            _retryDue.Remove(characterId);
            _saveStates.Remove(characterId);
        }
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
        _loadingBodies.Remove(body);
        _saveDue.Remove(body);
        _saveDirtySince.Remove(body);
        _playerBindings.Remove(body);
        if (_boundCharacters.GetValueOrDefault(binding.CharacterId) == body)
            _boundCharacters.Remove(binding.CharacterId);
        if (!keepSaveState && !binding.State.InFlight && binding.State.Queued == null)
            _saveStates.Remove(binding.CharacterId);
    }

    private void BindOwner(
        EntityUid body,
        NCMedicalStableOwnerKind kind,
        string key,
        long revision)
    {
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
        public bool InFlight;
        public bool Blocked;
        public PendingWrite? Queued;
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
