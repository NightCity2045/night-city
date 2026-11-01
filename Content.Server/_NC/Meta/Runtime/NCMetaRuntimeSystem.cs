// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Diagnostics;
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Authority;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Meta.Runtime;

public sealed class NCMetaRuntimeInstance
{
    public required EntityUid Host { get; init; }
    public required string Owner { get; init; }
    public required VerifiedArtifact Artifact { get; init; }
    public required MetaProcess Process { get; init; }
    public required NCMetaInstallation Installation { get; init; }
    public Guid Generation { get; } = Guid.NewGuid();
    public NCSessionEpoch WorldEpoch { get; init; }
    public ulong HostEpoch { get; init; }
    public ulong AuthorityEpoch { get; init; }
    public long? EntitlementExpiresAtTicks { get; init; }
    public NCMetaRunContactRequirement? MarketplaceContact { get; init; }
    public required NCMetaServiceAdmission ServiceAdmission { get; init; }
    internal NCMetaManagementProof? AttendingProof { get; init; }
    internal bool BootHealthReported;
    internal bool BootCompleted;
    internal MetaStopReason NativeStopReason;
    internal readonly Dictionary<ulong, NCMetaRuntimeBinding> Bindings = [];
    internal readonly Queue<string> Logs = [];
    internal long MetricsPendingAt;
    internal readonly MetaRateLimit Commands = new(8, 4);
    internal readonly MetaRateLimit Files = new(2, 2);
    internal readonly MetaRateLimit LogRate = new(20, 10);
    internal EntityUid? InvocationActor;
    internal Func<bool>? InvocationAuthorization;
    internal INCMetaDeferredInvocationAuthority? InvocationDeferredAuthority;
    internal Action<MetaProcess, MetaValue?>? Completion;
    internal readonly Dictionary<ulong, EntityUid> Actors = [];
    internal readonly Dictionary<ulong, INCMetaDeferredInvocationAuthority> ActorAuthorities = [];
    public string ProcessId => Generation.ToString("N");
}

internal sealed record NCMetaRuntimeBinding(NCMetaDeviceEntry Device, MetaGrant Grant, string Capability,
    Guid GrantId, string Slot, string ContractHash);

/// <summary>Owns guest execution. Guest code can only enqueue commands for next-tick native admission.</summary>
public sealed partial class NCMetaRuntimeSystem : EntitySystem
{
    [Dependency] private NCMetaAuthoritySystem _serviceAuthority = default!;
    private readonly Dictionary<Guid, HashSet<NCMetaRuntimeInstance>> _serviceInstallations = [];
    public event Action<NCMetaRuntimeInstance>? RuntimeStopped;
    public event Action<NCMetaRuntimeInstance, bool, string>? RuntimeBootHealthChanged;
    public event Action<EntityUid, EntityUid?, bool>? DoorCommandAccepted;
    public event Action<EntityUid>? ViewStateChanged;
    private sealed class HostAdapter(NCMetaRuntimeSystem system) : IMetaHost
    {
        public NCMetaRuntimeInstance Instance = default!;
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => system.Read(Instance, request);
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => system.TryQueue(Instance, request);
        public void Log(MetaProcess process, string text)
        {
            if (!Instance.LogRate.TryConsume(system._now)) return;
            if (Instance.Logs.Count == 32) Instance.Logs.Dequeue();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 256)
            {
                var builder = new System.Text.StringBuilder();
                var bytes = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > 256) break;
                    bytes += rune.Utf8SequenceLength; builder.Append(rune);
                }
                text = builder.ToString();
            }
            Instance.Logs.Enqueue(text);
        }
        public void Cancel(MetaProcess process, long operationId) { }
    }
    private sealed class Intent(NCMetaRuntimeInstance runtime, NCMetaRuntimeBinding binding, MetaHostRequest request,
        long id, long enqueuedAt, long enqueuedTick, long memoryLease)
    {
        public readonly NCMetaRuntimeInstance Runtime = runtime;
        public readonly NCMetaRuntimeBinding Binding = binding;
        public readonly MetaHostRequest Request = request;
        public readonly long Id = id;
        public readonly long EnqueuedAt = enqueuedAt;
        public readonly long EnqueuedTick = enqueuedTick;
        public readonly long MemoryLease = memoryLease;
        public volatile bool Cancelled;
        public bool Committed;
        public int CompletionQueued;
    }
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private MetaScheduler _scheduler = new();
    private readonly Dictionary<(EntityUid Host, Guid Installation), NCMetaRuntimeInstance> _instances = [];
    private readonly Dictionary<EntityUid, HashSet<NCMetaRuntimeInstance>> _hosts = [];
    private readonly Dictionary<string, NCMetaRuntimeInstance> _processes = new(StringComparer.Ordinal);
    private sealed record StoppedRuntime(string Owner, string[] Logs);
    private readonly Dictionary<(EntityUid Host, Guid Installation), StoppedRuntime> _history = [];
    private readonly Queue<(EntityUid Host, Guid Installation)> _historyOrder = [];
    private readonly Dictionary<EntityUid, HashSet<NCMetaRuntimeInstance>> _dependants = [];
    private readonly HashSet<NCMetaRuntimeInstance> _invocations = [];
    private readonly Dictionary<string, INCMetaDeviceAdapter> _adapters = new(StringComparer.Ordinal);
    private Queue<Intent> _intents = [];
    private readonly ConcurrentQueue<NativeCompletion> _completed = new();
    private readonly Queue<(NCMetaRuntimeInstance Runtime, bool Healthy, string Reason)> _bootNotices = [];
    private NCSessionEpoch _epoch;
    // Separate cold server lifetimes, while retaining at least 2^62 ordered round transitions of headroom.
    private long _clockEpoch = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long)), 0) & ((1L << 62) - 1);
    private long _now;
    private long _operation;
    private long _sequence;
    private ulong _eventAuthority;
    public MetaSchedulerTickResult? LastTick { get; private set; }
    /// <summary>Read-only scheduler/SampleStamp namespace; durable authority uses the opaque native world UUID.</summary>
    public long SchedulerEpoch => _clockEpoch;
    public object? GetDiagnosticSnapshot(EntityUid host) => _hosts.TryGetValue(host, out var instances)
        ? instances.Select(instance => new { installationId = instance.Installation.Id, generation = instance.Generation, artifactDigest = instance.Artifact.Digest,
            state = instance.Process.State.ToString(), memoryBytes = instance.Process.MemoryBytes,
            pendingOperation = instance.Process.PendingOperationId != 0, debugPaused = instance.Process.DebugPaused,
            stopReason = instance.Process.StopReason.ToString(), fault = instance.Process.Fault?.Code.ToString() }).ToArray()
        : null;
    public void SuspendAll()
    {
        foreach (var runtime in _instances.Values.ToArray()) Stop(runtime, MetaStopReason.Stop);
        _bootNotices.Clear();
        _intents.Clear();
    }

    private void EnterWorldEpoch(NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        if (_epoch == epoch) return;
        SuspendAll();
        // Active-world time is restored per world session. A closed session's timers,
        // quotas and continuations must not survive into a new session's scheduler.
        _scheduler = new MetaScheduler { Metrics = _metrics };
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        // Native epochs are opaque UUIDs. The scheduler requires an ordered local generation,
        // while all persistent authority and asynchronous proofs retain the original UUID.
        _clockEpoch = checked(_clockEpoch + 1);
        _epoch = epoch;
    }

    public override void Initialize()
    {
        base.Initialize();
        InitializeOperationalControls();
        _devices.Changed += OnDeviceChanged;
        _serviceAuthority.ServiceAuthorityChanged += OnServiceAuthorityChanged;
        InitializeServiceAttendance();
        SubscribeLocalEvent<DoorComponent, DoorStateChangedEvent>(OnDoorStateChanged);
        InitializeMarketplaceContacts();
    }

    public override void Shutdown()
    {
        ShutdownOperationalControls();
        _devices.Changed -= OnDeviceChanged;
        _serviceAuthority.ServiceAuthorityChanged -= OnServiceAuthorityChanged;
        ShutdownServiceAttendance();
        ShutdownMarketplaceContacts();
        foreach (var instance in _instances.Values.ToArray()) Stop(instance, MetaStopReason.Stop);
        _bootNotices.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        using var elapsed = MetaMetricScope.Measure(_metrics, MetaMetric.RuntimeElapsed, MetricTick);
        if (!_persistence.TryCaptureTime(out var epoch, out var time)) return;
        EnterWorldEpoch(epoch, time);
        _now = time.Ticks / TimeSpan.TicksPerMillisecond;
        StopExpiredEntitlements(epoch, time);
        UpdateMarketplaceContacts();
        using (MetaMetricScope.Measure(_metrics, MetaMetric.HardwareBeforeElapsed, MetricTick)) _hardware.BeforeRuntime(_now);
        _scheduler.SetClock(_clockEpoch, _timing.CurTick.Value, _timing.TickPeriod.TotalMilliseconds);
        _committedThisTick = 0;
        LastTick = _scheduler.RunTick(_now, 16384, TimeSpan.FromMilliseconds(2),
            boundedAdmission: StageNativeWakes, beforeSlice: CanRunAuthorizedSlice);
        MetaMetrics.Record(_metrics, MetaMetric.GasUsed, LastTick.GasUsed, MetricTick);
        MetaMetrics.Record(_metrics, MetaMetric.Slices, LastTick.Slices, MetricTick);
        for (var work = 0; work < 32 && _scheduler.TryDequeueTerminated(out var terminal); work++)
            if (_processes.TryGetValue(terminal.ProcessId, out var process)) Stop(process, terminal.StopReason);
        foreach (var instance in _processes.Values.ToArray())
            if (!instance.BootHealthReported && (instance.BootCompleted || instance.Process.State == MetaProcessState.Idle)) ReportBootHealth(instance, true, "Ready");
        using (MetaMetricScope.Measure(_metrics, MetaMetric.HardwareAfterElapsed, MetricTick)) _hardware.AfterRuntime(_now);
        PublishBootHealth();
        SamplePerformanceCounters();
    }

    public bool TryGetInstallation(EntityUid host, string owner, out NCMetaRuntimeInstance runtime) =>
        TryGetSingle(host, out runtime) && runtime.Owner == owner && IsCurrent(runtime);

    public bool TryGetInstallation(EntityUid host, string owner, Guid installationId, out NCMetaRuntimeInstance runtime) =>
        _instances.TryGetValue((host, installationId), out runtime!) && runtime.Owner == owner && IsCurrent(runtime);

    public bool TryGetGeneration(EntityUid host, string owner, Guid runtimeGeneration, out NCMetaRuntimeInstance runtime) =>
        _processes.TryGetValue(runtimeGeneration.ToString("N"), out runtime!) && runtime.Host == host && runtime.Owner == owner && IsCurrent(runtime);

    private bool TryGetSingle(EntityUid host, out NCMetaRuntimeInstance runtime)
    {
        runtime = default!;
        if (!_hosts.TryGetValue(host, out var instances) || instances.Count != 1) return false;
        runtime = instances.First(); return true;
    }

    public int GetProcessCount(EntityUid host) => _hosts.TryGetValue(host, out var instances) ? instances.Count : 0;
    public long GetReservedMemoryBytes(EntityUid host) => _devices.TryGetEntry(host, out var device) &&
        _scheduler.TryGetDeviceUsage(device.Id, out var usage) ? usage.ReservedMemoryBytes : 0;
    public bool TryGetHardwareUsage(string deviceId, out MetaDeviceUsage usage) => _scheduler.TryGetDeviceUsage(deviceId, out usage);
    public bool TrySetHardwareThrottle(string deviceId, int permille) => _scheduler.TrySetDeviceThrottle(deviceId, permille);
    public bool ReleaseHardwareQuota(string deviceId) => _scheduler.TryReleaseDevice(deviceId);
    public void StopHost(EntityUid host, MetaStopReason reason = MetaStopReason.Stop)
    {
        if (_hosts.TryGetValue(host, out var instances)) foreach (var instance in instances.ToArray()) Stop(instance, reason);
    }

    public void StopInstallation(EntityUid host, Guid installationId, MetaStopReason reason = MetaStopReason.Stop)
    {
        if (_instances.TryGetValue((host, installationId), out var instance)) Stop(instance, reason);
    }

    public bool CanInstall(EntityUid host, EntityUid user, VerifiedArtifact artifact, NCMetaBinding[] bindings)
    {
        return _devices.CanManage(host, user) && _devices.TryGetEntry(host, out var device) &&
               CanInstallOwned(host, device.Owner, artifact, bindings);
    }

    public bool CanInstallFirmware(EntityUid host, EntityUid user, VerifiedArtifact artifact, NCMetaBinding[] bindings) =>
        _devices.CanManage(host, user) && _hardware.CanStageFirmware(host, user) && _devices.TryGetEntry(host, out var device) &&
        CanInstallOwned(host, device.Owner, artifact, bindings, "Firmware", firmwareStaging: true);

    /// <summary>Admission for an existing slot; running approved firmware does not require a service session.</summary>
    public bool CanRunInstallation(EntityUid host, EntityUid user, NCMetaInstallation installation, VerifiedArtifact artifact,
        NCMetaBinding[] bindings) => _devices.CanManage(host, user) &&
        CanInstallOwned(host, installation.Owner, artifact, bindings, installation.SlotKind);

    private bool CanInstallOwned(EntityUid host, string owner, VerifiedArtifact artifact, NCMetaBinding[] bindings,
        string slotKind = "User", bool firmwareStaging = false)
    {
        if (IsOperationallyBlocked(artifact) || !_devices.TryGetEntry(host, out var device) || !device.Ready || device.Owner != owner || !_devices.IsPowered(host) ||
            !TryComp<NCMetaHostComponent>(host, out var hardware) || hardware.Disabled ||
            !_prototypes.TryIndex(hardware.Profile, out var profile) ||
            slotKind is not ("User" or "Firmware") || slotKind == "User" && !profile.AllowUserPackages ||
            firmwareStaging && (slotKind != "Firmware" || !profile.AllowFirmwareUpdates) ||
            !(firmwareStaging ? _hardware.CanRun(host, profile) || _hardware.IsMaintenance(host) && !_hardware.HasHardwareFault(host) : _hardware.CanRun(host, profile)) ||
            artifact.Manifest.RuntimeAbi != profile.RuntimeAbi || artifact.Manifest.LanguageVersion != profile.LanguageVersion ||
            bindings.Length != artifact.GetLinks().Length ||
            bindings.Any(binding => binding == null || binding.Slot == null || binding.Scopes == null) ||
            bindings.Select(binding => binding.Slot).Distinct(StringComparer.Ordinal).Count() != bindings.Length)
            return false;
        foreach (var link in artifact.GetLinks())
        {
            var binding = bindings.SingleOrDefault(value => value.Slot == link.SlotName);
            var capability = artifact.GetType(link.CapabilityTypeId);
            if (binding == null || binding.ContractHash != link.ContractHash ||
                !profile.Interfaces.Contains(capability.Name) || !_devices.TryResolve(binding.DeviceId, out var target) ||
                !target.Ready || target.Owner != owner || !_devices.IsPowered(target.Entity) || !HasInterface(target.Entity, capability.Name) ||
                !CanReach(host, target.Entity))
                return false;
            var requested = MetaApiCatalog.Default.Members.Where(member => member.Owner == capability.Name &&
                artifact.Manifest.RequiredScopes.Contains(member.Scope)).Select(member => member.Scope);
            if (requested.Any(scope => !binding.Scopes.Contains(scope, StringComparer.Ordinal))) return false;
        }
        return true;
    }

    public bool TryStart(EntityUid host, EntityUid user, NCMetaInstallation installation, VerifiedArtifact artifact,
        bool retainForInvocations = false, long? entitlementExpiresAtTicks = null, NCMetaRunContactRequirement? marketplaceContact = null,
        NCMetaServiceAdmission? serviceAdmission = null)
    {
        if (installation.Active.Digest != artifact.ContentHash) return false;
        return CanRunInstallation(host, user, installation, artifact, installation.Bindings) &&
               TryStartValidated(host, installation, artifact, retainForInvocations, entitlementExpiresAtTicks, marketplaceContact, serviceAdmission, user);
    }

    /// <summary>Cold boot uses only the already durable installation grants, never a fabricated actor.</summary>
    public bool TryStartRecovered(EntityUid host, NCMetaInstallation installation, VerifiedArtifact artifact, bool retainForInvocations,
        long? entitlementExpiresAtTicks = null, NCMetaRunContactRequirement? marketplaceContact = null,
        NCMetaServiceAdmission? serviceAdmission = null) =>
        installation.AutoStart && _hardware.CanAutoStart(host) && installation.Active.Digest == artifact.ContentHash &&
        TryStartValidated(host, installation, artifact, retainForInvocations, entitlementExpiresAtTicks, marketplaceContact, serviceAdmission, null);

    private bool TryStartValidated(EntityUid host, NCMetaInstallation installation, VerifiedArtifact artifact, bool retainForInvocations,
        long? entitlementExpiresAtTicks, NCMetaRunContactRequirement? marketplaceContact,
        NCMetaServiceAdmission? serviceAdmission, EntityUid? actor)
    {
        // Stopped generations awaiting their once-only health notification retain artifact metadata until the
        // post-VM phase. Do not admit replacement residency on top of that pending batch.
        if (serviceAdmission == null || serviceAdmission.InstallationId != installation.Id ||
            serviceAdmission.HostDeviceId != installation.HostDeviceId || serviceAdmission.ArtifactDigest != artifact.ContentHash ||
            serviceAdmission.Principal != installation.Owner || !_serviceAuthority.CanContinue(serviceAdmission) ||
            _bootNotices.Count != 0 || !CanInstallOwned(host, installation.Owner, artifact, installation.Bindings, installation.SlotKind) || !_devices.TryGetEntry(host, out var device) ||
            device.Id != installation.HostDeviceId || device.Owner != installation.Owner ||
            !_persistence.TryCaptureTime(out var epoch, out var time) || !TryComp<NCMetaHostComponent>(host, out var hardware))
            return false;
        NCMetaManagementProof? attending = null;
        if (!serviceAdmission.AllowUnattended && (actor == null ||
            !_serviceAuthority.TryGetManagementProof(actor.Value, installation.Owner, out attending) ||
            !_serviceAuthority.CanAttendService(attending))) return false;
        EnterWorldEpoch(epoch, time);
        var profile = _prototypes.Index(hardware.Profile);
        if (installation.Id == Guid.Empty || installation.SlotKind is not ("User" or "Firmware") ||
            installation.SlotKind == "User" && !profile.AllowUserPackages) return false;
        _instances.TryGetValue((host, installation.Id), out var old);
        var reserved = GetReservedMemoryBytes(host) - (old?.Process.MemoryReservationBytes ?? 0);
        var count = GetProcessCount(host) - (old == null ? 0 : 1);
        if (!MetaMemoryAdmission.TryPlan(profile.MemoryBytes, profile.MaxProcesses, artifact.ResidentMemoryBytes,
            artifact.MinimumProcessMemoryBytes, installation.MemoryLimitBytes, reserved, out var memory)) return false;
        if (count >= profile.MaxProcesses ||
            artifact.MaximumConstantStringBytes > 32768 ||
            installation.SlotKind == "Firmware" && _hosts.TryGetValue(host, out var slots) &&
                slots.Any(value => value != old && value.Installation.SlotKind == "Firmware") ||
            !_scheduler.TryConfigureDevice(device.Id, new(profile.MaxProcesses, profile.MemoryBytes, profile.GasPerWindow, profile.MaxBurstGas))) return false;
        _scheduler.TrySetDeviceThrottle(device.Id, _hardware.GetThrottlePermille(host));
        var adapter = new HostAdapter(this);
        var process = new MetaProcess(artifact, adapter, new MetaProcessLimits
        {
            MemoryBytes = memory, Frames = Math.Min(64, profile.MaxFrames),
            CollectionItems = 4096, StringBytes = 32768,
            RetainForInvocations = retainForInvocations,
        });
        process.Metrics = _metrics;
        var instance = new NCMetaRuntimeInstance
        {
            Host = host, Owner = installation.Owner, Artifact = artifact, Process = process,
            Installation = installation, WorldEpoch = epoch, HostEpoch = device.DeviceEpoch,
            AuthorityEpoch = device.AuthorityEpoch,
            EntitlementExpiresAtTicks = entitlementExpiresAtTicks,
            MarketplaceContact = marketplaceContact,
            ServiceAdmission = serviceAdmission,
            AttendingProof = attending,
        };
        adapter.Instance = instance;
        process.EventAuthorityReleased += token => { instance.Actors.Remove(token); instance.ActorAuthorities.Remove(token); };
        var nativeWork = new RuntimeWork(this, instance);
        process.ActivationCompleted += watch =>
        {
            if (!watch) instance.BootCompleted = true;
            else { nativeWork.Dirty = true; _scheduler.NotifyContinuation(instance.ProcessId); }
        };
        foreach (var link in artifact.GetLinks())
        {
            var config = installation.Bindings.Single(binding => binding.Slot == link.SlotName);
            if (!_devices.TryResolve(config.DeviceId, out var target)) return false;
            if (_dependants.TryGetValue(target.Entity, out var subscribers) && subscribers.Count >= 64) return false;
            var token = (ulong) link.Id + 1;
            var capability = artifact.GetType(link.CapabilityTypeId).Name;
            if (serviceAdmission.GetBindingReference(link.SlotName) is not { } reference) return false;
            var allowed = MetaApiCatalog.Default.Members.Where(member => member.Owner == capability).Select(member => member.Scope);
            var stamp = new MetaAuthorityStamp(installation.Id, instance.Generation, installation.Owner, target.Id,
                target.DeviceEpoch, target.AuthorityEpoch, serviceAdmission.ExpiresAtTicks is { } expires
                    ? expires / TimeSpan.TicksPerMillisecond : long.MaxValue);
            instance.Bindings.Add(token, new NCMetaRuntimeBinding(target, new MetaGrant(stamp, config.Scopes,
                artifact.Manifest.RequiredScopes, allowed), capability, reference.GrantId, link.SlotName, link.ContractHash));
            if (!process.TryBindLink(link.Id, token)) return false;
        }
        process.SetClock(_clockEpoch, _timing.CurTick.Value, _timing.TickPeriod.TotalMilliseconds);
        if (!process.TryStartMain()) return false;
        if (old != null) Stop(old, MetaStopReason.Stop);
        _runtimeWork.Add(instance.ProcessId, nativeWork);
        if (!_scheduler.TryRegister(instance.ProcessId, instance.Owner, device.Id, process,
                deviceWeight: Math.Clamp(profile.CpuWeight, 1, 16), continuations: nativeWork))
        { _runtimeWork.Remove(instance.ProcessId); return false; }
        if (!TryTrackEntitlement(instance))
        {
            _scheduler.Remove(instance.ProcessId);
            _runtimeWork.Remove(instance.ProcessId);
            process.Stop(MetaStopReason.PermissionRevoked);
            return false;
        }
        if (!TryTrackMarketplaceContact(instance))
        {
            UntrackEntitlement(instance);
            _scheduler.Remove(instance.ProcessId);
            _runtimeWork.Remove(instance.ProcessId);
            process.Stop(MetaStopReason.PermissionRevoked);
            return false;
        }
        _instances.Add((host, installation.Id), instance);
        if (!_hosts.TryGetValue(host, out var running)) _hosts.Add(host, running = []);
        running.Add(instance);
        _processes.Add(instance.ProcessId, instance);
        if (!_serviceInstallations.TryGetValue(installation.Id, out var approved)) _serviceInstallations.Add(installation.Id, approved = []);
        approved.Add(instance);
        TrackServiceAttendance(instance);
        _history.Remove((host, installation.Id));
        AddDependant(host, instance);
        foreach (var binding in instance.Bindings.Values) AddDependant(binding.Device.Entity, instance);
        return true;
    }

    public bool TryStop(EntityUid host, EntityUid user)
    {
        if (!_devices.CanManage(host, user) || !TryGetSingle(host, out var instance)) return false;
        Stop(instance, MetaStopReason.Stop);
        return true;
    }

    public bool TryStop(EntityUid host, EntityUid user, Guid installationId)
    {
        if (!_devices.CanManage(host, user) || !_instances.TryGetValue((host, installationId), out var instance)) return false;
        Stop(instance, MetaStopReason.Stop); return true;
    }

    public bool TryDebug(EntityUid host, EntityUid user, Guid installationId, Func<MetaProcess, bool> action)
    {
        if (!_devices.CanManage(host, user) || !_instances.TryGetValue((host, installationId), out var instance) || !IsCurrent(instance)) return false;
        var changed = action(instance.Process);
        if (changed) _scheduler.NotifyRunnable(instance.ProcessId);
        return changed;
    }

    public string[] GetLogs(EntityUid host, EntityUid user, Guid installationId)
    {
        if (!_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device)) return [];
        if (_instances.TryGetValue((host, installationId), out var instance)) return instance.Logs.ToArray();
        return _history.TryGetValue((host, installationId), out var stopped) && stopped.Owner == device.Owner ? stopped.Logs.ToArray() : [];
    }

    public string[] GetLogs(EntityUid host, EntityUid user)
    {
        if (!_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device)) return [];
        if (TryGetSingle(host, out var instance)) return instance.Logs.ToArray();
        if (_hosts.ContainsKey(host)) return [];
        var stopped = _history.Where(pair => pair.Key.Host == host && pair.Value.Owner == device.Owner).Take(2).ToArray();
        return stopped.Length == 1 ? stopped[0].Value.Logs.ToArray() : [];
    }

    public bool TryInvoke(EntityUid host, EntityUid user, int functionId,
        Func<MetaProcess, IReadOnlyList<MetaValue>> createArgs, Action<MetaProcess, MetaValue?> completed) =>
        TryInvokeAuthorized(host, user, functionId, createArgs, completed, () => _devices.CanManage(host, user),
            CaptureOwnerInvocationAuthority(host, user));

    public bool TryDebug(EntityUid host, EntityUid user, Func<MetaProcess, bool> action)
    {
        if (!_devices.CanManage(host, user) || !TryGetSingle(host, out var runtime) || !IsCurrent(runtime)) return false;
        var changed = action(runtime.Process);
        if (changed) _scheduler.NotifyRunnable(runtime.ProcessId);
        return changed;
    }

    public bool TryInvokeAuthorized(EntityUid host, EntityUid user, int functionId,
        Func<MetaProcess, IReadOnlyList<MetaValue>> createArgs, Action<MetaProcess, MetaValue?> completed, Func<bool> canInvoke,
        INCMetaDeferredInvocationAuthority? deferredAuthority = null)
    {
        return TryGetSingle(host, out var instance) && TryInvokeAuthorized(instance, user, functionId, createArgs, completed, canInvoke, deferredAuthority);
    }

    public bool TryInvokeAuthorized(EntityUid host, Guid runtimeGeneration, EntityUid user, int functionId,
        Func<MetaProcess, IReadOnlyList<MetaValue>> createArgs, Action<MetaProcess, MetaValue?> completed, Func<bool> canInvoke,
        INCMetaDeferredInvocationAuthority? deferredAuthority = null) =>
        _processes.TryGetValue(runtimeGeneration.ToString("N"), out var instance) && instance.Host == host &&
        TryInvokeAuthorized(instance, user, functionId, createArgs, completed, canInvoke, deferredAuthority);

    private bool TryInvokeAuthorized(NCMetaRuntimeInstance instance, EntityUid user, int functionId,
        Func<MetaProcess, IReadOnlyList<MetaValue>> createArgs, Action<MetaProcess, MetaValue?> completed, Func<bool> canInvoke,
        INCMetaDeferredInvocationAuthority? deferredAuthority)
    {
        // Compatibility entry: acceptance is queued; native work and guest execution both use the one tick budget.
        if (!IsCurrent(instance) || !canInvoke() || instance.Process.State != MetaProcessState.Idle || _invocations.Count >= 64 ||
            !_runtimeWork.TryGetValue(instance.ProcessId, out var work) || work.Legacy != null || work.Application != null ||
            !_scheduler.TryReserveAuxiliaryMemory(instance.ProcessId, 16384, out var lease)) return false;
        work.Legacy = new(user, functionId, createArgs, completed, canInvoke, deferredAuthority, lease);
        _scheduler.NotifyContinuation(instance.ProcessId); return true;
    }

    private bool HasInterface(EntityUid uid, string capability) =>
        capability == "meta.files.FileStore" && HasComp<NCMetaStorageComponent>(uid) ||
        capability == "meta.doors.Door" && HasComp<DoorComponent>(uid) ||
        capability == "meta.doors.DoorPanel" && HasComp<NCMetaDoorPanelComponent>(uid) ||
        TryComp<NCMetaDeviceComponent>(uid, out var device) && device.Interfaces.Contains(capability) &&
        _adapters.Values.Distinct().Any(adapter => adapter.CanBind(uid, capability));

    public bool CanReach(EntityUid host, EntityUid target)
    {
        return _network.CanRoute(host, target);
    }

    private bool IsCurrent(NCMetaRuntimeInstance instance) =>
        _serviceAuthority.CanContinue(instance.ServiceAdmission) &&
        (instance.ServiceAdmission.AllowUnattended || instance.AttendingProof != null && _serviceAuthority.CanAttendService(instance.AttendingProof)) &&
        HasRunEntitlement(instance) &&
        HasMarketplaceContact(instance) &&
        _instances.TryGetValue((instance.Host, instance.Installation.Id), out var registered) && registered == instance &&
        instance.Process.State is not (MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) &&
        _devices.TryGetEntry(instance.Host, out var entry) && entry.Id == instance.Installation.HostDeviceId &&
        entry.Owner == instance.Owner && entry.DeviceEpoch == instance.HostEpoch && entry.AuthorityEpoch == instance.AuthorityEpoch &&
        _devices.IsPowered(instance.Host) && _persistence.TryCaptureTime(out var epoch, out _) && epoch == instance.WorldEpoch &&
        TryComp<NCMetaHostComponent>(instance.Host, out var host) && !host.Disabled &&
        _prototypes.TryIndex(host.Profile, out var profile) && _hardware.CanRun(instance.Host, profile);

    private bool CanUse(NCMetaRuntimeInstance instance, NCMetaRuntimeBinding binding, MetaApiMember member) =>
        IsCurrent(instance) && _devices.TryResolve(binding.Device.Id, out var target) && target.Owner == instance.Owner &&
        _devices.IsPowered(target.Entity) && _hardware.CanUseDevice(target.Entity) && CanReach(instance.Host, target.Entity) &&
        binding.Grant.CanUse(member.Scope, instance.Generation, target.Id, target.DeviceEpoch, target.AuthorityEpoch, _now) &&
        _serviceAuthority.CanUse(instance.ServiceAdmission, binding.GrantId, binding.Slot, target.Id, binding.ContractHash, member) &&
        (instance.InvocationAuthorization == null || instance.InvocationAuthorization());

    private MetaValue Read(NCMetaRuntimeInstance instance, MetaHostRequest request)
    {
        using var elapsed = MetaMetricScope.Measure(_metrics, MetaMetric.HostReadElapsed, MetricTick);
        var import = instance.Process.GetImport(request.ImportId);
        var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == import.Name);
        if (!instance.Process.TryGetHostToken(request.Capability, out var token) ||
            !instance.Bindings.TryGetValue(token, out var binding) || !CanUse(instance, binding, member))
            return instance.Process.CreateErrorResult(import.ReturnType!.Value, "PermissionRevoked");
        if (_adapters.TryGetValue(import.Name, out var adapter))
            return adapter.Read(new NCMetaDeviceInvocation(instance, binding.Device.Entity, binding.Device.Id,
                request, member, 0, GetActor(instance)));
        return import.Name switch
        {
            "meta.doors.Door.snapshot" when TryComp<DoorComponent>(binding.Device.Entity, out var door) =>
                instance.Process.CreateResult(import.ReturnType!.Value, DoorStateValue(instance.Process, door)),
            _ => instance.Process.CreateErrorResult(import.ReturnType!.Value, "Unsupported"),
        };
    }

    private MetaHostResult TryQueue(NCMetaRuntimeInstance instance, MetaHostRequest request)
    {
        var process = instance.Process;
        var import = process.GetImport(request.ImportId);
        var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == import.Name);
        MetaHostResult Error(string code)
        {
            MetaMetrics.Record(_metrics, MetaMetric.CommandsRejected);
            AuditNative(instance, instance.Installation.HostDeviceId, import.Name, 0, NCMetaAuditPhase.Rejected);
            return MetaHostResult.Completed(process.CreateErrorResult(import.ReturnType!.Value, code));
        }
        if (!process.TryGetHostToken(request.Capability, out var token) || !instance.Bindings.TryGetValue(token, out var binding) ||
            !CanUse(instance, binding, member)) return Error("PermissionRevoked");
        if (_pendingNative.Count >= 256 || !instance.Commands.TryConsume(_now)) return Error("RateLimited");
        if (member.Owner == "meta.files.FileStore" && !instance.Files.TryConsume(_now)) return Error("RateLimited");
        if (_operation == long.MaxValue) return Error("QuotaExceeded");
        var retained = member.Owner == "meta.files.FileStore" ? 140L * 1024 : 16384L;
        retained += 256 + request.Arguments.Count * 16L;
        if (!_scheduler.TryReserveAuxiliaryMemory(instance.ProcessId, retained, out var memoryLease)) return Error("QuotaExceeded");
        var id = ++_operation;
        instance.MetricsPendingAt = MetaMetrics.Timestamp(_metrics);
        var intent = new Intent(instance, binding, request with { Arguments = request.Arguments.ToArray() }, id,
            instance.MetricsPendingAt, _timing.CurTick.Value, memoryLease);
        _pendingNative.Add(id, intent); _runtimeWork[instance.ProcessId].PendingIntent = intent;
        _intents.Enqueue(intent); _scheduler.NotifyContinuationNextTick(instance.ProcessId);
        MetaMetrics.Record(_metrics, MetaMetric.CommandsAccepted);
        AuditNative(instance, binding.Device.Id, import.Name, id, NCMetaAuditPhase.Queued);
        return MetaHostResult.Awaiting(id);
    }

    private void CommitIntent(Intent intent)
    {
        var runtime = intent.Runtime; var process = runtime.Process;
        if (process.State != MetaProcessState.Waiting || process.PendingOperationId != intent.Id) { FinishIntent(intent); return; }
        using var elapsed = MetaMetricScope.Measure(_metrics, MetaMetric.HostCommitElapsed, MetricTick);
        MetaMetrics.Latency(_metrics, MetaMetric.CommandAdmissionLatency, intent.EnqueuedAt, intent.Id);
        var import = process.GetImport(intent.Request.ImportId);
        var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == import.Name);
        if (!CanUse(runtime, intent.Binding, member)) { Complete(intent, process.CreateErrorResult(import.ReturnType!.Value, "PermissionRevoked")); return; }
        AuditNative(runtime, intent.Binding.Device.Id, import.Name, intent.Id, NCMetaAuditPhase.Committing);
        try
        {
            if (member.Owner == "meta.files.FileStore") { BeginFileOperation(intent); return; }
            if (import.Name == "meta.doors.Door.setOpen" && TryComp<DoorComponent>(intent.Binding.Device.Entity, out var door))
            {
                var actor = GetActor(runtime);
                var service = TryComp<NCMetaDeviceComponent>(intent.Binding.Device.Entity, out var device) && device.AllowServiceControl;
                if (actor == null && !service || actor != null && !_doors.HasAccess(intent.Binding.Device.Entity, actor, door))
                { Complete(intent, process.CreateErrorResult(import.ReturnType!.Value, "AccessDenied")); return; }
                var accepted = intent.Request.Arguments[0].AsBool()
                    ? _doors.TryOpen(intent.Binding.Device.Entity, door, actor, quiet: true)
                    : _doors.TryClose(intent.Binding.Device.Entity, door, actor);
                if (accepted) DoorCommandAccepted?.Invoke(intent.Binding.Device.Entity, actor, intent.Request.Arguments[0].AsBool());
                Complete(intent, accepted ? process.CreateResult(import.ReturnType!.Value, DoorStateValue(process, door))
                    : process.CreateErrorResult(import.ReturnType!.Value, "Interlock"));
            }
            else if (_adapters.TryGetValue(import.Name, out var adapter))
            {
                var result = adapter.Commit(new NCMetaDeviceInvocation(runtime, intent.Binding.Device.Entity,
                    intent.Binding.Device.Id, intent.Request, member, intent.Id, GetActor(runtime)));
                if (!result.Pending) Complete(intent, result.Value);
                else if (result.OperationId != intent.Id) Stop(runtime, MetaStopReason.InternalFault);
            }
            else Complete(intent, process.CreateErrorResult(import.ReturnType!.Value, "Unsupported"));
        }
        catch (Exception exception)
        {
            Log.Error($"META native command failed for {runtime.Generation}: {exception}");
            Stop(runtime, MetaStopReason.InternalFault);
        }
    }

    private void BeginFileOperation(Intent intent)
    {
        var runtime = intent.Runtime; var process = runtime.Process;
        var path = process.GetString(intent.Request.Arguments[0]);
        var member = process.GetImport(intent.Request.ImportId);
        if (!MetaPaths.IsValid(path) || !TryComp<NCMetaStorageComponent>(intent.Binding.Device.Entity, out var storage) ||
            !_persistence.TryCaptureTime(out var epoch, out var time))
        { Complete(intent, process.CreateErrorResult(member.ReturnType!.Value, "InvalidArgument")); return; }
        var volumeId = intent.Binding.Device.Id == runtime.Installation.HostDeviceId && runtime.Installation.StateVolumeId != Guid.Empty
            ? runtime.Installation.StateVolumeId : NCMetaPersistenceSystem.StableGuid("volume:" + intent.Binding.Device.Id);
        var text = intent.Request.Arguments.Count == 2 ? process.GetString(intent.Request.Arguments[1]) : null;
        if (text != null && (!TryComp<NCMetaHostComponent>(runtime.Host, out var hardware) ||
            !_prototypes.TryIndex(hardware.Profile, out var profile) || Encoding.UTF8.GetByteCount(text) > profile.MaxFileBytes))
        { Complete(intent, process.CreateErrorResult(member.ReturnType!.Value, "QuotaExceeded")); return; }
        var authorization = new NCMetaFileWriteAuthorization(intent.Binding.Device.Id, intent.Binding.Device.AuthorityEpoch,
            runtime.Installation, runtime.AuthorityEpoch);
        _ = FileOperationAsync(intent, volumeId, path, text, storage.CapacityBytes, epoch, time, authorization);
    }

    private async Task FileOperationAsync(Intent intent, Guid volume, string path, string? text, int capacity,
        NCSessionEpoch epoch, NCActiveWorldTime time, NCMetaFileWriteAuthorization authorization)
    {
        try
        {
            var read = text == null ? await _persistence.ReadTextAsync(volume, intent.Runtime.Owner, path) : null;
            var write = text != null ? await _persistence.WriteTextAsync(NCMetaPersistenceSystem.StableGuid(
                "write:" + intent.Runtime.ProcessId + ":" + intent.Id), volume, intent.Runtime.Owner, path, text, capacity, epoch, time,
                () => CanCommitFileAsync(intent), authorization) : null;
            EnqueueNativeCompletion(intent, () =>
            {
                var process = intent.Runtime.Process;
                var import = process.GetImport(intent.Request.ImportId);
                var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == import.Name);
                if (!CanUse(intent.Runtime, intent.Binding, member) || process.State != MetaProcessState.Waiting || process.PendingOperationId != intent.Id) return;
                Complete(intent, text == null ? read == null ? process.CreateErrorResult(import.ReturnType!.Value, "NotFound") :
                    process.CreateResult(import.ReturnType!.Value, process.CreateString(read)) : write?.Success == true ?
                    process.CreateResult(import.ReturnType!.Value, MetaValue.Unit) : process.CreateErrorResult(import.ReturnType!.Value,
                        write?.MessageKey == "nc-meta-error-quota" ? "QuotaExceeded" : "IoFailure"));
            });
        }
        catch (NCMetaBlobQuotaException)
        {
            EnqueueNativeCompletion(intent, () =>
            {
                if (!IsCurrent(intent.Runtime)) return;
                var process = intent.Runtime.Process;
                var import = process.GetImport(intent.Request.ImportId);
                if (process.State == MetaProcessState.Waiting && process.PendingOperationId == intent.Id)
                    Complete(intent, process.CreateErrorResult(import.ReturnType!.Value, "QuotaExceeded"));
            });
        }
        catch (Exception exception)
        {
            Log.Error($"META file operation failed: {exception}");
            EnqueueNativeCompletion(intent, () => { if (IsCurrent(intent.Runtime)) Stop(intent.Runtime, MetaStopReason.InternalFault); });
        }
    }

    private Task<bool> CanCommitFileAsync(Intent intent)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() =>
        {
            var import = intent.Runtime.Process.GetImport(intent.Request.ImportId);
            var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == import.Name);
            completion.TrySetResult(CanUse(intent.Runtime, intent.Binding, member));
        });
        return completion.Task;
    }

    private void Complete(Intent intent, MetaValue value)
    {
        if (!_scheduler.QueueCompletion(intent.Runtime.ProcessId, intent.Id, value)) { FinishIntent(intent); return; }
        FinishIntent(intent);
        MetaMetrics.Record(_metrics, MetaMetric.CompletionsAccepted);
        MetaMetrics.Latency(_metrics, MetaMetric.CompletionLatency, intent.EnqueuedAt, intent.Id);
        intent.Runtime.MetricsPendingAt = 0;
        AuditNative(intent.Runtime, intent.Binding.Device.Id, intent.Runtime.Process.GetImport(intent.Request.ImportId).Name, intent.Id, NCMetaAuditPhase.Completed);
    }

    private MetaValue Stamp(MetaProcess process) => process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"),
        [MetaValue.Int(_clockEpoch), MetaValue.Int(++_sequence), MetaValue.Int(_timing.CurTick.Value)]);
    private MetaValue DoorStateValue(MetaProcess process, DoorComponent door)
    {
        var phase = door.State switch { DoorState.Closed => 0, DoorState.Opening => 1, DoorState.Open => 2, DoorState.Closing => 3, _ => 4 };
        return process.CreateRecord(process.GetTypeId("meta.doors.DoorState"),
            [MetaValue.Bool(door.State is DoorState.Open or DoorState.Opening), process.CreateEnum(process.GetTypeId("meta.doors.DoorPhase"), phase), Stamp(process)]);
    }

    private void OnDoorStateChanged(Entity<DoorComponent> ent, ref DoorStateChangedEvent args)
    {
        if (!_dependants.TryGetValue(ent.Owner, out var dependants)) return;
        foreach (var runtime in dependants.ToArray())
        {
            foreach (var watch in runtime.Artifact.GetWatches())
            {
                var link = runtime.Artifact.GetLinks()[watch.LinkId];
                if (!runtime.Bindings.TryGetValue((ulong) link.Id + 1, out var binding) || binding.Device.Entity != ent.Owner ||
                    runtime.Artifact.GetImport(watch.EventImportId).Name != "meta.doors.Door.stateChanged") continue;
                var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == "meta.doors.Door.stateChanged");
                if (!CanUse(runtime, binding, member)) continue;
                try { _scheduler.TryEnqueueEvent(runtime.ProcessId, Array.IndexOf(runtime.Artifact.GetWatches(), watch),
                    [DoorStateValue(runtime.Process, ent.Comp)], ++_sequence); }
                catch (Exception) { Stop(runtime, MetaStopReason.InternalFault); }
            }
        }
    }

    private void AddDependant(EntityUid uid, NCMetaRuntimeInstance instance)
    {
        if (!_dependants.TryGetValue(uid, out var set)) _dependants.Add(uid, set = []);
        set.Add(instance);
    }
    private void OnDeviceChanged(NCMetaDeviceEntry entry)
    {
        if (!_dependants.TryGetValue(entry.Entity, out var instances)) return;
        foreach (var instance in instances.ToArray()) Stop(instance, MetaStopReason.PermissionRevoked);
    }
    private void Stop(NCMetaRuntimeInstance instance, MetaStopReason reason)
    {
        if (!_instances.TryGetValue((instance.Host, instance.Installation.Id), out var registered) || registered != instance) return;
        AuditNative(instance, instance.Installation.HostDeviceId, "", 0, NCMetaAuditPhase.Stopped);
        bool? bootHealth = instance.BootHealthReported ? null : instance.BootCompleted ? true :
            instance.Process.State is MetaProcessState.Faulted or MetaProcessState.Quarantined ||
            reason is MetaStopReason.InternalFault or MetaStopReason.Runaway or MetaStopReason.GcWorkExceeded ? false : null;
        foreach (var binding in instance.Bindings.Values) binding.Grant.Revoke();
        UntrackEntitlement(instance);
        UntrackMarketplaceContact(instance);
        instance.NativeStopReason = reason;
        instance.Process.Stop(reason);
        instance.InvocationActor = null;
        instance.InvocationAuthorization = null;
        instance.Completion = null;
        instance.InvocationDeferredAuthority = null;
        instance.ActorAuthorities.Clear();
        _scheduler.Remove(instance.ProcessId);
        _runtimeWork.Remove(instance.ProcessId);
        _instances.Remove((instance.Host, instance.Installation.Id));
        if (_hosts.TryGetValue(instance.Host, out var hostInstances) && hostInstances.Remove(instance) && hostInstances.Count == 0) _hosts.Remove(instance.Host);
        _processes.Remove(instance.ProcessId);
        UntrackServiceAttendance(instance);
        if (_serviceInstallations.TryGetValue(instance.Installation.Id, out var approved) && approved.Remove(instance) && approved.Count == 0)
            _serviceInstallations.Remove(instance.Installation.Id);
        var historyKey = (instance.Host, instance.Installation.Id);
        if (!_history.ContainsKey(historyKey))
        {
            while (_historyOrder.Count >= 128) _history.Remove(_historyOrder.Dequeue());
            _historyOrder.Enqueue(historyKey);
        }
        _history[historyKey] = new(instance.Owner, instance.Logs.ToArray());
        _invocations.Remove(instance);
        foreach (var uid in instance.Bindings.Values.Select(binding => binding.Device.Entity).Append(instance.Host).Distinct())
            if (_dependants.TryGetValue(uid, out var set) && set.Remove(instance) && set.Count == 0) _dependants.Remove(uid);
        if (bootHealth is { } healthy) ReportBootHealth(instance, healthy, healthy ? "Ready" : reason.ToString());
        if (RuntimeStopped != null)
            foreach (Action<NCMetaRuntimeInstance> subscriber in RuntimeStopped.GetInvocationList())
                try { subscriber(instance); }
                catch (Exception exception) { Log.Error($"META native controller cleanup failed: {exception}"); }
    }

    private void ReportBootHealth(NCMetaRuntimeInstance instance, bool healthy, string reason)
    {
        if (instance.BootHealthReported) return;
        instance.BootHealthReported = true;
        _bootNotices.Enqueue((instance, healthy, reason));
    }

    private void PublishBootHealth()
    {
        // Admission above prevents accumulating stopped generations across batches. Drain the fixed live-process
        // ceiling only after all guest execution and native completion phases have returned.
        for (var work = 0; work < 4096 && _bootNotices.TryDequeue(out var notice); work++)
        {
            if (RuntimeBootHealthChanged == null) continue;
            foreach (Action<NCMetaRuntimeInstance, bool, string> subscriber in RuntimeBootHealthChanged.GetInvocationList())
                try { subscriber(notice.Runtime, notice.Healthy, notice.Reason); }
                catch (Exception exception) { Log.Error($"META boot health callback failed: {exception}"); }
        }
    }

    public void RegisterAdapter(INCMetaDeviceAdapter adapter)
    {
        foreach (var name in adapter.Members)
        {
            if (!MetaApiCatalog.Default.Members.Any(member => member.QualifiedName == name && member.Effect != "Event") ||
                !_adapters.TryAdd(name, adapter))
                throw new InvalidOperationException("Duplicate or unknown META native adapter member.");
        }
    }

    public bool TryComplete(NCMetaRuntimeInstance runtime, long operationId, Func<MetaProcess, MetaValue> createValue)
    {
        if (!IsCurrent(runtime) || runtime.Process.State != MetaProcessState.Waiting ||
            runtime.Process.PendingOperationId != operationId || !_pendingNative.TryGetValue(operationId, out var intent) ||
            !ReferenceEquals(intent.Runtime, runtime) || intent.Cancelled) return false;
        EnqueueNativeCompletion(intent, () =>
        {
            if (!CanCompleteIntent(intent)) return;
            try { Complete(intent, createValue(runtime.Process)); }
            catch (Exception exception) { Log.Error($"META native completion failed: {exception}"); Stop(runtime, MetaStopReason.InternalFault); }
        });
        return true;
    }

    public bool CanContinue(NCMetaDeviceInvocation invocation, bool requireWaiting = true) =>
        invocation.Runtime.Process.State is not (MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) &&
        (!requireWaiting || invocation.Runtime.Process.State == MetaProcessState.Waiting && invocation.Runtime.Process.PendingOperationId == invocation.OperationId) &&
        invocation.Runtime.Bindings.Values.Any(binding => binding.Device.Id == invocation.DeviceId &&
            binding.Device.Entity == invocation.Device && CanUse(invocation.Runtime, binding, invocation.Member));

    public int TryPublishEvent(EntityUid device, string eventName, Func<MetaProcess, MetaValue> createArgument, EntityUid? actor = null,
        ulong contextToken = 0, INCMetaDeferredInvocationAuthority? deferredAuthority = null)
    {
        using var elapsed = MetaMetricScope.Measure(_metrics, MetaMetric.EventIngressElapsed, MetricTick);
        if (!_dependants.TryGetValue(device, out var dependants)) return 0;
        var accepted = 0;
        foreach (var runtime in dependants.ToArray())
        {
            var watches = runtime.Artifact.GetWatches();
            for (var index = 0; index < watches.Length; index++)
            {
                var watch = watches[index];
                if (runtime.Artifact.GetImport(watch.EventImportId).Name != eventName ||
                    !runtime.Bindings.TryGetValue((ulong) watch.LinkId + 1, out var binding) || binding.Device.Entity != device)
                    continue;
                var member = MetaApiCatalog.Default.Members.Single(value => value.QualifiedName == eventName);
                if (!CanUse(runtime, binding, member)) continue;
                ulong token = 0;
                if (actor != null)
                {
                    if (runtime.Actors.Count >= 65 || _eventAuthority == ulong.MaxValue) continue;
                    token = ++_eventAuthority;
                    runtime.Actors.Add(token, actor.Value);
                    if (deferredAuthority != null) runtime.ActorAuthorities.Add(token, deferredAuthority);
                }
                try
                {
                    if (_scheduler.TryEnqueueEvent(runtime.ProcessId, index, [createArgument(runtime.Process)], ++_sequence, token, contextToken)) accepted++;
                    else if (token != 0) { runtime.Actors.Remove(token); runtime.ActorAuthorities.Remove(token); }
                }
                catch (Exception) { if (token != 0) { runtime.Actors.Remove(token); runtime.ActorAuthorities.Remove(token); } Stop(runtime, MetaStopReason.InternalFault); }
            }
        }
        return accepted;
    }

    public MetaValue CreateStamp(MetaProcess process) => Stamp(process);
    private EntityUid? GetActor(NCMetaRuntimeInstance runtime) => runtime.InvocationActor ??
        (runtime.Actors.TryGetValue(runtime.Process.CurrentEventAuthorityToken, out var actor) && Exists(actor) ? actor : null);
}
