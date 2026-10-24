// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Projects;
using Content.Shared._NC.Meta;
using Robust.Shared.Timing;
using Content.Server._NC.Persistence.RoundSnapshot;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Drains durable authoring before the common snapshot; execution stacks and handles are never captured.</summary>
public sealed partial class NCMetaRecoverySystem : EntitySystem
{
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaNetrunSystem _netrun = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCMetaWorkspaceSystem _workspace = default!;
    [Dependency] private NCMetaSnapshotSystem _snapshots = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private Robust.Shared.Prototypes.IPrototypeManager _profiles = default!;
    private readonly Dictionary<EntityUid, (string Id, string Owner, int Attempts, TimeSpan Due)> _pending = [];
    private readonly Queue<EntityUid> _ready = [];
    private readonly Dictionary<EntityUid, Guid> _restoring = [];
    private Guid _generation = Guid.NewGuid();
    private bool _drained = true;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnClosing, before: [typeof(NCRoundSnapshotCoordinatorSystem)]);
        SubscribeLocalEvent<NCRoundSnapshotCaptureEvent>(OnCapture);
        _devices.Changed += OnDeviceChanged;
        _hardware.HostReady += OnHostReady;
        _runtime.RuntimeBootHealthChanged += OnBootHealth;
    }
    public override void Shutdown()
    {
        _devices.Changed -= OnDeviceChanged;
        _hardware.HostReady -= OnHostReady;
        _runtime.RuntimeBootHealthChanged -= OnBootHealth;
        base.Shutdown();
    }
    private void OnHostReady(EntityUid host) { if (_devices.TryGetEntry(host, out var entry)) OnDeviceChanged(entry); }
    private void OnBootHealth(NCMetaRuntimeInstance runtime, bool healthy, string reason)
    {
        if (!_persistence.TryCaptureTime(out var epoch, out var time)) return;
        var allowRecovery = runtime.Installation.SlotKind != "Firmware" ||
            TryComp<NCMetaHostComponent>(runtime.Host, out var host) && _profiles.TryIndex(host.Profile, out var profile) && profile.RecoverFirmwareOnBootFailure;
        _ = SaveHealthAsync(runtime, healthy, epoch, time, allowRecovery);
    }
    private async Task SaveHealthAsync(NCMetaRuntimeInstance runtime, bool healthy,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch, Content.Shared._NC.Persistence.Time.NCActiveWorldTime time, bool allowRecovery)
    {
        var generation = _generation;
        try
        {
            var saved = await _persistence.CompleteInstallationHealthAsync(runtime.Installation, healthy, epoch, time, allowRecovery);
            if (!saved || healthy || !allowRecovery || runtime.Installation.SlotKind != "Firmware") return;
            _tasks.RunOnMainThread(() =>
            {
                if (generation != _generation || !_persistence.TryCaptureTime(out var current, out _) || current != epoch ||
                    !_devices.TryGetEntry(runtime.Host, out var device) || device.Owner != runtime.Owner) return;
                if (_runtime.TryGetGeneration(runtime.Host, runtime.Owner, runtime.Generation, out _))
                    _runtime.StopInstallation(runtime.Host, runtime.Installation.Id);
                OnDeviceChanged(device);
            });
        }
        catch (Exception exception) { Log.Error($"META install health could not be saved for {runtime.Installation.Id}: {exception}"); }
    }
    private void OnDeviceChanged(NCMetaDeviceEntry entry)
    {
        if (!entry.Available || !entry.Ready || entry.Owner.Length == 0 || !HasComp<NCMetaHostComponent>(entry.Entity) ||
            !_hardware.CanAutoStart(entry.Entity) || _pending.ContainsKey(entry.Entity) || _pending.Count >= 4096) return;
        _pending.Add(entry.Entity, (entry.Id, entry.Owner, 0, _timing.CurTime));
        _ready.Enqueue(entry.Entity);
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_drained || !_persistence.TryCaptureTime(out _, out _) || _restoring.Count >= 2) return;
        var count = Math.Min(4, _ready.Count);
        for (var work = 0; work < count && _restoring.Count < 2; work++)
        {
            var uid = _ready.Dequeue();
            if (!_pending.TryGetValue(uid, out var pending)) continue;
            if (pending.Due > _timing.CurTime) { _ready.Enqueue(uid); continue; }
            _restoring.Add(uid, _generation);
            _ = RestoreAsync(uid, pending.Id, pending.Owner, pending.Attempts, _generation);
        }
    }
    private async Task RestoreAsync(EntityUid uid, string id, string owner, int attempts, Guid generation)
    {
        var success = false;
        try { success = await _workspace.TryRestoreInstallationAsync(uid, id, owner); }
        catch (Exception exception) { Log.Error($"META cold boot failed for {id}: {exception}"); }
        _tasks.RunOnMainThread(() =>
        {
            if (generation != _generation) return;
            _restoring.Remove(uid);
            if (!_pending.TryGetValue(uid, out var pending) || pending.Id != id || pending.Owner != owner) return;
            if (success || attempts >= 15 || !Exists(uid)) { _pending.Remove(uid); return; }
            _pending[uid] = (id, owner, attempts + 1, _timing.CurTime + TimeSpan.FromSeconds(2));
            _ready.Enqueue(uid);
        });
    }
    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        _generation = Guid.NewGuid(); _pending.Clear(); _ready.Clear(); _restoring.Clear();
        _persistence.ResumeIngress(); _netrun.ResumeSessions(); _drained = true;
        foreach (var entry in _devices.GetRegistered()) OnDeviceChanged(entry);
    }
    private void OnClosing(NCActiveWorldSessionClosingEvent args)
    {
        _netrun.QuiesceSessions();
        _generation = Guid.NewGuid(); _pending.Clear(); _ready.Clear(); _restoring.Clear();
        _runtime.SuspendAll();
        _drained = false;
        try
        {
            var drain = _persistence.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(5));
            _tasks.BlockWaitOnTask(drain);
            drain.GetAwaiter().GetResult();
            var references = _snapshots.PrepareCaptureAsync().WaitAsync(TimeSpan.FromSeconds(30));
            _tasks.BlockWaitOnTask(references);
            references.GetAwaiter().GetResult();
            _drained = true;
        }
        catch (Exception exception)
        {
            Log.Error($"META did not drain before world capture: {exception}");
            args.AddPendingWrite(Task.FromException(new InvalidOperationException("META snapshot drain failed.")));
        }
    }
    private void OnCapture(NCRoundSnapshotCaptureEvent args)
    {
        if (!_drained) { args.Fail("META authoring was not durably drained."); return; }
        _snapshots.Capture(args);
        var entries = _devices.GetRegistered().Select(entry => new DeviceReference(entry.Id, entry.Owner)).ToArray();
        if (entries.Length > 16384) { args.Fail("META device snapshot count limit."); return; }
        var chunk = 0;
        foreach (var group in entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).Chunk(128))
            args.TryAddSection("meta-devices", "world", "city", 1, group.Length, JsonSerializer.Serialize(group), chunk++);
        if (entries.Length == 0) args.TryAddSection("meta-devices", "world", "city", 1, 0, "[]");
    }
    private sealed record DeviceReference(string Id, string Owner);
}
