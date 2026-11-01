// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.RoundSnapshot;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Immutable META checkpoint references, selected once for city objects and late inventory alike.</summary>
public sealed partial class NCMetaSnapshotSystem : EntitySystem
{
    private const string SectionType = NCMetaCheckpointIntegrity.SectionType;
    private const int MaximumRecords = 131072;
    private const int MaximumBytes = 96 * 1024 * 1024;
    private const int MaximumSectionBytes = 4 * 1024 * 1024;
    private static readonly string[] AggregateTypes =
    [
        "meta.device", "meta.economic-device", "meta.project", "meta.project-tombstone", "meta.installation-index", "meta.installation",
        "meta.installation-stage", "meta.volume", "meta.media", "meta.software-copy-index",
        "meta.software.product", "meta.software.publication", "meta.software.identity", "meta.software.publisher-image",
        "meta.software.license", "meta.software.owner", "meta.software.device", "meta.software.catalog",
        "meta.software.release-index", "meta.software.copy-evidence", "meta.software.resale", "meta.software.resale-recipient", "meta.software.inheritance", "meta.inherited-images", "meta.service-grants",
        "meta.net.policy", "meta.net.journal",
        "meta.clinical.operation", "meta.machine.operation", "meta.drone.operation", "meta.operation",
        "meta.site-publication", "meta.hosting-quote", "meta.software.quote", "meta.software.command", "meta.quote-pool",
        "meta.development-contract", "meta.development.delivery", "meta.development.delivery-claim",
        "meta.development.acceptance", "meta.development.party", "meta.development.inbox", "meta.development.command",
        "meta.development.catalog",
        "meta.hosting-lease", "meta.hosting-offer", "meta.hosting-domain", "meta.hosting-capacity", "meta.hosting-owner",
    ];
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCRoundRestoreContextSystem _restore = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private IReadOnlyDictionary<(string Type, string Id), NCAggregateSnapshotData> _anchors =
        new Dictionary<(string, string), NCAggregateSnapshotData>();
    private IReadOnlyList<(string Payload, int Count)> _capture = [];
    private bool _restoreValid;
    private bool _disposed;
    private TaskCompletionSource _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _verifiedLock = new();
    private readonly Dictionary<(string Type, string Id), NCAggregateSnapshotData> _verified = [];
    private readonly SemaphoreSlim _tailReaders = new(2, 2);
    private int _tailAdmissions;
    private Guid _generation = Guid.NewGuid();
    public Guid? SelectedSnapshotId { get; private set; }
    public Guid SelectionGeneration => _generation;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted,
            after: [typeof(NCRoundRestoreContextSystem)], before: [typeof(NCMetaRecoverySystem)]);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnClosed);
    }

    public override void Shutdown() { _disposed = true; _restoreValid = false; _selection.TrySetResult(); base.Shutdown(); }

    private void OnClosed(NCActiveWorldSessionClosedEvent args)
    {
        _restoreValid = false;
        _generation = Guid.NewGuid();
        _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_verifiedLock) _verified.Clear();
    }

    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        _capture = [];
        _generation = Guid.NewGuid();
        lock (_verifiedLock) _verified.Clear();
        _anchors = new Dictionary<(string, string), NCAggregateSnapshotData>();
        SelectedSnapshotId = _restore.Manifest?.SnapshotId;
        _restoreValid = !_restore.SelectionFailed && SelectedSnapshotId == null;
        if (_restore.SelectionFailed) { _selection.TrySetResult(); return; }
        if (SelectedSnapshotId is not { } selected) { _selection.TrySetResult(); return; }
        try
        {
            var read = LoadAnchorsAsync(selected).WaitAsync(TimeSpan.FromSeconds(30));
            _tasks.BlockWaitOnTask(read);
            _anchors = read.GetAwaiter().GetResult();
            _restoreValid = true;
        }
        catch (Exception exception) { Log.Error($"META checkpoint {selected} could not be validated: {exception}"); }
        finally { _selection.TrySetResult(); }
    }

    private async Task<IReadOnlyDictionary<(string, string), NCAggregateSnapshotData>> LoadAnchorsAsync(Guid selected)
    {
        var manifest = _restore.Manifest;
        var inventory = await _database.GetNCRoundSnapshotInventoryAsync(selected);
        if (manifest == null || inventory == null || !NCRoundSnapshotInventoryIntegrity.CanValidate(inventory, manifest))
            throw new InvalidOperationException("Selected world checkpoint inventory is incomplete or invalid.");
        var sections = await _database.GetNCRoundSnapshotSectionsAsync(selected, SectionType, "world", "city");
        var expected = inventory.Sections.Where(value => value.SectionType == SectionType && value.OwnerType == "world" && value.OwnerId == "city").ToArray();
        if (expected.Length != sections.Count || sections.Any(section => !expected.Any(value => value.ChunkIndex == section.ChunkIndex &&
                value.FormatVersion == section.FormatVersion && value.EntityCount == section.EntityCount && value.Checksum == section.Checksum)))
            throw new InvalidOperationException("META checkpoint chunks do not match the selected immutable inventory.");
        var descriptors = inventory.Sections.Where(value => value.SectionType == NCMetaCheckpointIntegrity.DescriptorType).ToArray();
        if (descriptors.Length != 0 || sections.Any(value => value.FormatVersion == 2))
        {
            if (descriptors is not [{ OwnerType: "world", OwnerId: "city", ChunkIndex: 0, FormatVersion: 1 }])
                throw new InvalidOperationException("Missing or duplicate META checkpoint descriptor.");
            var stored = await _database.GetNCRoundSnapshotSectionsAsync(selected, NCMetaCheckpointIntegrity.DescriptorType, "world", "city");
            if (stored.Count != 1 || Encoding.UTF8.GetByteCount(stored[0].Payload) > 1024 * 1024 ||
                NCMetaJournalIntegrity.HashPayload(stored[0].Payload) != descriptors[0].Checksum)
                throw new InvalidOperationException("META checkpoint descriptor content is invalid.");
            var descriptor = JsonSerializer.Deserialize<NCMetaCheckpointDescriptor>(stored[0].Payload);
            if (descriptor == null || descriptor.Records != descriptors[0].EntityCount || !NCMetaCheckpointIntegrity.CanValidate(descriptor, sections))
                throw new InvalidOperationException("META checkpoint descriptor does not match all chunks and records.");
        }
        var result = new Dictionary<(string, string), NCAggregateSnapshotData>();
        long bytes = 0;
        var chunk = 0;
        foreach (var section in sections)
        {
            var size = Encoding.UTF8.GetByteCount(section.Payload);
            bytes += size;
            if (section.SnapshotId != selected || section.FormatVersion is not (1 or 2) || section.ChunkIndex != chunk++ ||
                size > MaximumSectionBytes || bytes > MaximumBytes ||
                NCRoundSnapshotCaptureEvent.Hash(section.Payload) != section.Checksum)
                throw new InvalidOperationException("Invalid META checkpoint section.");
            var records = JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(section.Payload) ??
                throw new InvalidOperationException("Missing META checkpoint records.");
            if (records.Length != section.EntityCount) throw new InvalidOperationException("META checkpoint count mismatch.");
            foreach (var record in records)
                if (record.AggregateVersion < 1 || record.FormatVersion < 1 || record.AggregateId.Length is < 1 or > 128 ||
                    !AggregateTypes.Contains(record.AggregateType, StringComparer.Ordinal) || result.Count >= MaximumRecords ||
                    !result.TryAdd((record.AggregateType, record.AggregateId), record))
                    throw new InvalidOperationException("Invalid or duplicate META checkpoint reference.");
        }
        return result;
    }

    /// <summary>Called after durable ingress drains; capture itself performs no database I/O.</summary>
    public async Task PrepareCaptureAsync()
    {
        var sections = new List<(string, int)>();
        var group = new List<NCAggregateSnapshotData>();
        var records = 0;
        var groupBytes = 2;
        long bytes = 0;
        void Flush()
        {
            if (group.Count == 0) return;
            var payload = JsonSerializer.Serialize(group);
            var size = Encoding.UTF8.GetByteCount(payload);
            if (size > MaximumSectionBytes || (bytes += size) > MaximumBytes)
                throw new InvalidOperationException("META checkpoint byte limit.");
            sections.Add((payload, group.Count));
            group.Clear();
            groupBytes = 2;
        }
        foreach (var type in AggregateTypes)
        {
            string? cursor = null;
            while (true)
            {
                var page = await _database.GetNCAggregateSnapshotPageAsync(type, cursor, 64);
                foreach (var record in page)
                {
                    if (++records > MaximumRecords) throw new InvalidOperationException("META checkpoint record limit.");
                    var validated = await ReadCurrentAsync(record.AggregateType, record.AggregateId);
                    if (validated == null || validated != record) throw new InvalidOperationException("META checkpoint contains unverified journal state.");
                    var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(record));
                    if (group.Count != 0 && groupBytes + size + 1 > MaximumSectionBytes)
                        Flush();
                    group.Add(record);
                    groupBytes += size + (group.Count > 1 ? 1 : 0);
                    if (group.Count == 64) Flush();
                }
                if (page.Count < 64) break;
                cursor = page[^1].AggregateId;
            }
        }
        Flush();
        if (sections.Count == 0) sections.Add(("[]", 0));
        _capture = sections;
    }

    public void Capture(NCRoundSnapshotCaptureEvent args)
    {
        if (_capture.Count == 0) { args.Fail("META immutable checkpoint references were not prepared."); return; }
        for (var i = 0; i < _capture.Count; i++)
            args.TryAddSection(SectionType, "world", "city", 2, _capture[i].Count, _capture[i].Payload, i);
        var descriptor = NCMetaCheckpointIntegrity.Describe(_capture);
        args.TryAddSection(NCMetaCheckpointIntegrity.DescriptorType, "world", "city", 1,
            descriptor.Records, JsonSerializer.Serialize(descriptor));
    }

    /// <summary>Confirmed journal tail remains authoritative; a missing/tampered baseline is never resurrected.</summary>
    public async Task<NCAggregateSnapshotData?> ReadCurrentAsync(string type, string id, CancellationToken cancellationToken = default)
    {
        await _selection.Task.WaitAsync(cancellationToken);
        if (_disposed) return null;
        if (!_restoreValid) throw new NCMetaSnapshotIntegrityException("Selected META checkpoint could not be authenticated.");
        var generation = _generation;
        void CheckSelection()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation || !_restoreValid || _disposed)
                throw new NCMetaSnapshotIntegrityException("META restore selection changed while reading an aggregate.");
        }
        var current = await _database.GetNCAggregateSnapshotAsync(type, id);
        CheckSelection();
        _anchors.TryGetValue((type, id), out var baseline);
        if (current == null)
        {
            var known = baseline != null;
            lock (_verifiedLock) known |= _verified.ContainsKey((type, id));
            if (known || generation != _generation || !_restoreValid)
                throw new NCMetaSnapshotIntegrityException("A verified META checkpoint aggregate is missing or its restore selection changed.");
            return null;
        }
        if (current.AggregateVersion < 1 || current.AggregateVersion == long.MaxValue || current.FormatVersion < 1 ||
            !current.SessionEpoch.IsValid || !current.ActiveWorldTime.IsValid || current.Payload == null ||
            current.AggregateType != type || current.AggregateId != id)
            throw new NCMetaSnapshotIntegrityException("META aggregate version is invalid.");
        lock (_verifiedLock)
            if (_verified.TryGetValue((type, id), out var cached))
            {
                if (cached.AggregateVersion > current.AggregateVersion)
                    throw new NCMetaSnapshotIntegrityException("A confirmed META aggregate version regressed.");
                if (cached.AggregateVersion >= (baseline?.AggregateVersion ?? 0)) baseline = cached;
            }
        var difference = current.AggregateVersion - (baseline?.AggregateVersion ?? 0);
        if (difference < 0) throw new NCMetaSnapshotIntegrityException("A selected META aggregate version regressed.");
        // Exact hot-cache hits need no worker/reader slot. A cold history is streamed rather than imposing a lifetime write limit.
        if (current != baseline)
        {
            if (Interlocked.Increment(ref _tailAdmissions) > 66) // Two active readers and at most 64 asynchronous waiters.
            {
                Interlocked.Decrement(ref _tailAdmissions);
                throw new InvalidOperationException("META journal verification is busy; retry the read.");
            }
            var held = false;
            try
            {
                await _tailReaders.WaitAsync(cancellationToken);
                held = true;
                CheckSelection();
                var verifier = await Task.Run(() => NCMetaJournalIntegrity.TailVerifier.Create(current, baseline), cancellationToken);
                if (verifier == null) throw new NCMetaSnapshotIntegrityException("META journal state identity is invalid.");
                while (verifier.Remaining != 0)
                {
                    CheckSelection();
                    var page = await _database.GetNCMetaJournalPageAsync(type, id, verifier.NextVersion + 1,
                        (int) Math.Min(NCMetaJournalIntegrity.MaximumPageEvents, verifier.Remaining), cancellationToken);
                    CheckSelection();
                    // Await normally: the next database call must retain the native synchronization context (including sync SQLite tests).
                    if (!await Task.Run(() => verifier.ConsumePage(page, cancellationToken), cancellationToken))
                        throw new NCMetaSnapshotIntegrityException("META journal tail is incomplete or does not attest the current content.");
                }
                if (!verifier.Complete) throw new NCMetaSnapshotIntegrityException("META journal tail does not reach its selected anchor.");
            }
            finally
            {
                if (held) _tailReaders.Release();
                Interlocked.Decrement(ref _tailAdmissions);
            }
        }
        CheckSelection();
        lock (_verifiedLock)
        {
            if (_verified.TryGetValue((type, id), out var confirmed) && confirmed.AggregateVersion > current.AggregateVersion)
                throw new NCMetaSnapshotIntegrityException("A confirmed META aggregate version regressed during verification.");
            if ((_verified.Count < MaximumRecords || _verified.ContainsKey((type, id))) &&
                (!_verified.TryGetValue((type, id), out var previous) || previous.AggregateVersion < current.AggregateVersion))
                _verified[(type, id)] = current;
        }
        return current;
    }
}
