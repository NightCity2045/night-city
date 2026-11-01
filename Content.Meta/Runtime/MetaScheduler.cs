// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using Content.Meta.Diagnostics;

namespace Content.Meta.Runtime;

public sealed record MetaSchedulerLimits
{
    public int Processes { get; init; } = 4096;
    public int ProcessesPerPrincipal { get; init; } = 256;
    public long ReservedMemoryBytes { get; init; } = 1024L * 1024 * 1024;
    public long PrincipalReservedMemoryBytes { get; init; } = 256L * 1024 * 1024;
    public int SliceGas { get; init; } = 256;
    public int Quantum { get; init; } = 256;
    public int MaximumDebt { get; init; } = 262144;
    public long WindowMilliseconds { get; init; } = 1000;
    public long PrincipalGasPerWindow { get; init; } = 1_000_000;
    public long DeviceGasPerWindow { get; init; } = 500_000;
    public long ProcessGasPerWindow { get; init; } = 250_000;
    public int MaximumDispatches { get; init; } = 4096;
    public int MaximumTimers { get; init; } = 4096;
}

public sealed record MetaSchedulerTickResult(int GasUsed, int Slices, int ControlActions, int Completions, int RunnablePrincipals)
{
    public int GuestGasUsed { get; init; }
    public int ContinuationGasUsed { get; init; }
    public int ContinuationSteps { get; init; }
    public bool WallExhausted { get; init; }
    public bool GasExhausted { get; init; }
}
public sealed record MetaProcessTermination(string ProcessId, MetaProcessState State, MetaStopReason StopReason, MetaFault? Fault);
/// <summary>Trusted physical hardware ceilings, shared by every principal using the same device identity.</summary>
public sealed record MetaDeviceQuota(int MaxProcesses, long MemoryBytes, long GasPerWindow, int MaxBurstGas);
public sealed record MetaDeviceUsage(int Processes, long ReservedMemoryBytes, long TotalGas, long WindowGas, int ThrottlePermille);

/// <summary>
/// Hierarchical deficit round-robin. Only runnable buckets and due timers are visited;
/// adding processes cannot multiply a principal's quantum or rolling-window quota.
/// Trusted host phases surround this pure scheduler's control and VM phases.
/// </summary>
public sealed class MetaScheduler
{
    public IMetaMetrics? Metrics { get; set; }
    private sealed class Window
    {
        public long Start = -1;
        public long Gas;
        public void Refresh(long now, long duration)
        {
            var start = now - now % duration;
            if (Start == start) return;
            Start = start; Gas = 0;
        }
    }
    private sealed class Principal(string id, int weight)
    {
        public readonly string Id = id;
        public readonly int Weight = weight;
        public readonly Dictionary<string, Device> Devices = new(StringComparer.Ordinal);
        public readonly Queue<Device> Ready = [];
        public readonly Window Window = new();
        public bool Enqueued;
        public bool Parked;
        public long Deficit;
        public long Reserved;
        public long AuxiliaryReserved;
        public int Processes;
        public bool Removed;
    }
    private sealed class Device(string id, Principal principal, int weight)
    {
        public readonly string Id = id;
        public readonly Principal Principal = principal;
        public readonly int Weight = weight;
        public readonly Queue<Registration> Ready = [];
        public readonly Window Window = new();
        public bool Enqueued;
        public bool Parked;
        public long Deficit;
        public int Processes;
        public bool Removed;
    }
    private sealed class Hardware(MetaDeviceQuota quota)
    {
        public MetaDeviceQuota Quota = quota;
        public readonly Window Window = new();
        public int Processes;
        public long Reserved;
        public long TotalGas;
        public int Throttle = 1000;
    }
    private sealed class Registration(string id, MetaProcess process, Device device, int weight)
    {
        public readonly string Id = id;
        public readonly MetaProcess Process = process;
        public readonly Device Device = device;
        public readonly int Weight = weight;
        public readonly Window Window = new();
        public long Deficit;
        public bool Enqueued;
        public bool Parked;
        public bool Removed;
        public bool DeadlineScheduled;
        public long DeadlineAt;
        public Action? DeadlineCallback;
        public Action? TerminationCallback;
        public MetaProcessTermination? Termination;
        public IMetaSchedulerContinuations? Continuations;
        public bool ContinuationPending;
        public bool InvocationActive;
        public bool PreferWatch;
        public bool PreferGuest;
        public bool Deferred;
        public bool Cancelled;
        public long AuxiliaryReserved;
        public readonly Dictionary<long, long> AuxiliaryLeases = [];
    }
    private readonly MetaSchedulerLimits _limits;
    private readonly Dictionary<string, Principal> _principals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Hardware> _hardware = new(StringComparer.Ordinal);
    private readonly Queue<Principal> _ready = [];
    private readonly Queue<Registration> _terminated = [];
    private readonly Dictionary<string, MetaStopReason> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Operation, MetaValue Value)> _completions = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Action, long> _timers = new();
    private readonly HashSet<string> _revokedArtifacts = new(StringComparer.Ordinal);
    private readonly List<Registration> _deferred = [];
    private readonly MetaContinuationPolicy _continuationPolicy;
    private readonly TimeProvider _time;
    private long _reserved;
    private long _auxiliaryReserved;
    private long _nextLease;
    private long _now;
    private bool _disabled;
    private long _clockEpoch;
    private long _clockTick;
    private double _clockPeriod = 1000.0 / 60;
    private bool _clockSet;
    private bool _requiresTickIdentity;
    private bool _runningTick;
    private long _tickStarted;
    private TimeSpan? _tickWallLimit;
    private (long Epoch, long Tick)? _lastRun;

    public int RegisteredProcesses => _processes.Count;
    public int RegisteredPrincipals => _principals.Count;
    public long ReservedMemoryBytes => _reserved;
    public long AuxiliaryReservedMemoryBytes => _auxiliaryReserved;
    public MetaContinuationPolicy ContinuationPolicy => _continuationPolicy;
    /// <summary>Read-only original deadline for bounded ingress bookkeeping; it cannot grant gas or restart the tick.</summary>
    public bool HasTimeRemaining => _runningTick && HasTime(_tickStarted, _tickWallLimit);
    public MetaScheduler(MetaSchedulerLimits? limits = null, MetaContinuationPolicy? continuationPolicy = null, TimeProvider? timeProvider = null)
    {
        _limits = limits ?? new MetaSchedulerLimits();
        _continuationPolicy = continuationPolicy ?? new MetaContinuationPolicy();
        _time = timeProvider ?? TimeProvider.System;
        if (_limits.SliceGas <= 0 || _limits.Quantum <= 0 || _limits.WindowMilliseconds <= 0 || _limits.MaximumDebt < _limits.SliceGas) throw new ArgumentOutOfRangeException(nameof(limits));
        if (_continuationPolicy.MinimumStepGas < 1 || _continuationPolicy.MaximumStepGas < _continuationPolicy.MinimumStepGas ||
            _continuationPolicy.MaximumStepGas > 262144 || _continuationPolicy.AuxiliaryLeasesPerProcess is < 1 or > 4096 ||
            _continuationPolicy.AuxiliaryBytesPerProcess <= 0 || _continuationPolicy.AuxiliaryBytesPerPrincipal <= 0 || _continuationPolicy.AuxiliaryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(continuationPolicy));
    }
    public void SetClock(long epoch, long tick, double millisecondsPerTick)
    {
        if (_runningTick) throw new InvalidOperationException("Cannot change the clock during a scheduler tick.");
        if (epoch < 0 || tick < 0 || !double.IsFinite(millisecondsPerTick) || millisecondsPerTick <= 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (_requiresTickIdentity && _lastRun is { } last && (epoch < last.Epoch || epoch == last.Epoch && tick < last.Tick))
            throw new ArgumentOutOfRangeException(nameof(tick));
        _clockEpoch = epoch; _clockTick = tick; _clockPeriod = millisecondsPerTick; _clockSet = true;
    }

    public bool TryConfigureDevice(string deviceId, MetaDeviceQuota quota)
    {
        if (string.IsNullOrEmpty(deviceId) || quota.MaxProcesses is < 1 or > 256 || quota.MemoryBytes <= 0 ||
            quota.MemoryBytes > _limits.ReservedMemoryBytes || quota.GasPerWindow <= 0 ||
            quota.GasPerWindow > _limits.DeviceGasPerWindow || quota.MaxBurstGas <= 0 || quota.MaxBurstGas > _limits.MaximumDebt) return false;
        if (_hardware.TryGetValue(deviceId, out var hardware))
        {
            if (hardware.Processes != 0 && hardware.Quota != quota) return false;
            hardware.Quota = quota; return true;
        }
        if (_processes.Values.Any(registration => registration.Device.Id == deviceId)) return false;
        if (_hardware.Count >= _limits.Processes) return false;
        _hardware.Add(deviceId, new Hardware(quota)); return true;
    }

    public bool TrySetDeviceThrottle(string deviceId, int permille)
    {
        if (permille is < 0 or > 1000 || !_hardware.TryGetValue(deviceId, out var hardware)) return false;
        hardware.Throttle = permille; return true;
    }

    public bool TryGetDeviceUsage(string deviceId, out MetaDeviceUsage usage)
    {
        if (!_hardware.TryGetValue(deviceId, out var hardware)) { usage = null!; return false; }
        hardware.Window.Refresh(_now, _limits.WindowMilliseconds);
        usage = new(hardware.Processes, hardware.Reserved, hardware.TotalGas, hardware.Window.Gas, hardware.Throttle); return true;
    }

    public bool TryReleaseDevice(string deviceId) => _hardware.TryGetValue(deviceId, out var hardware) &&
        hardware.Processes == 0 && _hardware.Remove(deviceId);

    public bool TryRegister(string processId, string principalId, string deviceId, MetaProcess process, int principalWeight = 1, int deviceWeight = 1, int processWeight = 1, IMetaSchedulerContinuations? continuations = null)
    {
        if (_disabled || process.State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined || string.IsNullOrEmpty(processId) || string.IsNullOrEmpty(principalId) || string.IsNullOrEmpty(deviceId) || principalWeight is < 1 or > 16 || deviceWeight is < 1 or > 16 || processWeight is < 1 or > 16 || _processes.ContainsKey(processId) || _processes.Count >= _limits.Processes || _timers.Count >= _limits.MaximumTimers * 4L || _revokedArtifacts.Contains(process.ArtifactDigest)) return false;
        var reserved = process.MemoryReservationBytes;
        if (reserved < 0 || reserved > _limits.ReservedMemoryBytes - _reserved || reserved > _limits.PrincipalReservedMemoryBytes || process.MaximumPrimitiveGas > _limits.MaximumDebt) return false;
        if (_hardware.TryGetValue(deviceId, out var physical) &&
            (physical.Processes >= physical.Quota.MaxProcesses || reserved > physical.Quota.MemoryBytes - physical.Reserved ||
             process.MaximumPrimitiveGas > physical.Quota.MaxBurstGas)) return false;
        if (!_principals.TryGetValue(principalId, out var principal))
        {
            principal = new Principal(principalId, principalWeight); _principals.Add(principalId, principal);
        }
        if (principal.Weight != principalWeight || principal.Processes >= _limits.ProcessesPerPrincipal || reserved > _limits.PrincipalReservedMemoryBytes - principal.Reserved) return false;
        if (!principal.Devices.TryGetValue(deviceId, out var device)) { device = new Device(deviceId, principal, deviceWeight); principal.Devices.Add(deviceId, device); }
        if (device.Weight != deviceWeight) return false;
        var registration = new Registration(processId, process, device, processWeight) { Continuations = continuations };
        registration.TerminationCallback = () =>
        {
            if (registration.Removed || registration.Termination != null) return;
            CancelContinuations(registration, process.StopReason);
            registration.Termination = new MetaProcessTermination(processId, process.State, process.StopReason, process.Fault);
            _terminated.Enqueue(registration);
        };
        process.Terminated += registration.TerminationCallback;
        _processes.Add(processId, registration); principal.Processes++; device.Processes++; principal.Reserved += reserved; _reserved += reserved;
        if (physical != null) { physical.Processes++; physical.Reserved += reserved; }
        _requiresTickIdentity |= continuations != null;
        NotifyRunnable(processId);
        return true;
    }

    public bool Remove(string processId)
    {
        if (!_processes.Remove(processId, out var registration)) return false;
        CancelContinuations(registration, registration.Process.StopReason == MetaStopReason.None ? MetaStopReason.Stop : registration.Process.StopReason);
        registration.Removed = true; registration.Enqueued = false;
        _deferred.Remove(registration);
        registration.Process.Terminated -= registration.TerminationCallback;
        RemoveFromQueue(_terminated, registration);
        _controls.Remove(processId); _completions.Remove(processId);
        registration.Device.Principal.Processes--; registration.Device.Principal.Reserved -= registration.Process.MemoryReservationBytes; _reserved -= registration.Process.MemoryReservationBytes;
        if (_hardware.TryGetValue(registration.Device.Id, out var physical)) { physical.Processes--; physical.Reserved -= registration.Process.MemoryReservationBytes; }
        if (registration.DeadlineScheduled) _timers.Remove(registration.DeadlineCallback!, out _, out _);
        var device = registration.Device;
        RemoveFromQueue(device.Ready, registration);
        if (--device.Processes == 0) { device.Removed = true; device.Principal.Devices.Remove(device.Id); RemoveFromQueue(device.Principal.Ready, device); device.Ready.Clear(); }
        if (device.Principal.Processes == 0) { device.Principal.Removed = true; _principals.Remove(device.Principal.Id); RemoveFromQueue(_ready, device.Principal); device.Principal.Ready.Clear(); }
        registration.Process.Stop(registration.Process.StopReason == MetaStopReason.None ? MetaStopReason.Stop : registration.Process.StopReason);
        return true;
    }

    /// <summary>Bounded once-only terminal notifications, including direct supervisor Stop and faults outside a scheduled slice.</summary>
    public bool TryDequeueTerminated(out MetaProcessTermination termination)
    {
        while (_terminated.TryDequeue(out var registration))
            if (!registration.Removed && registration.Termination is { } notice) { termination = notice; return true; }
        termination = null!; return false;
    }

    public void NotifyRunnable(string processId)
    {
        if (!_processes.TryGetValue(processId, out var registration) || registration.Removed) return;
        if (registration.Process.DebugPaused) { ScheduleDeadline(registration); return; }
        if (registration.Enqueued || registration.Parked || registration.Deferred || !HasWork(registration)) return;
        registration.Enqueued = true; registration.Device.Ready.Enqueue(registration);
        Enqueue(registration.Device);
    }

    /// <summary>One latched wake per registration; no extra HDRR bucket, quantum or invocation is created.</summary>
    public bool NotifyContinuation(string processId)
    {
        if (!_processes.TryGetValue(processId, out var registration) || registration.Cancelled || registration.Continuations == null || IsTerminal(registration.Process)) return false;
        registration.ContinuationPending = true;
        NotifyRunnable(processId); return true;
    }

    /// <summary>Wake on the next RunTick, preserving next-tick native intent staging and the current tick budget.</summary>
    public bool NotifyContinuationNextTick(string processId)
    {
        if (!NotifyContinuation(processId)) return false;
        var registration = _processes[processId];
        if (registration.Enqueued) { RemoveFromQueue(registration.Device.Ready, registration); registration.Enqueued = false; }
        Defer(registration); return true;
    }

    /// <summary>Reserve retained native buffers against the same global/principal/physical RAM as the VM.</summary>
    public bool TryReserveAuxiliaryMemory(string processId, long bytes, out long leaseId)
    {
        leaseId = 0;
        if (bytes <= 0 || _disabled || !_processes.TryGetValue(processId, out var registration) || registration.Cancelled ||
            IsTerminal(registration.Process) || _controls.ContainsKey(processId) || _revokedArtifacts.Contains(registration.Process.ArtifactDigest) ||
            registration.AuxiliaryLeases.Count >= _continuationPolicy.AuxiliaryLeasesPerProcess || _nextLease == long.MaxValue) return false;
        var principal = registration.Device.Principal;
        if (bytes > _continuationPolicy.AuxiliaryBytesPerProcess - registration.AuxiliaryReserved ||
            bytes > _continuationPolicy.AuxiliaryBytesPerPrincipal - principal.AuxiliaryReserved ||
            bytes > _continuationPolicy.AuxiliaryBytes - _auxiliaryReserved || bytes > _limits.ReservedMemoryBytes - _reserved ||
            bytes > _limits.PrincipalReservedMemoryBytes - principal.Reserved) return false;
        _hardware.TryGetValue(registration.Device.Id, out var physical);
        if (physical != null && bytes > physical.Quota.MemoryBytes - physical.Reserved) return false;
        leaseId = ++_nextLease; registration.AuxiliaryLeases.Add(leaseId, bytes);
        registration.AuxiliaryReserved += bytes; principal.AuxiliaryReserved += bytes;
        _auxiliaryReserved += bytes; principal.Reserved += bytes; _reserved += bytes;
        if (physical != null) physical.Reserved += bytes;
        return true;
    }

    public bool TryReleaseAuxiliaryMemory(string processId, long leaseId)
    {
        if (!_processes.TryGetValue(processId, out var registration) || !registration.AuxiliaryLeases.Remove(leaseId, out var bytes)) return false;
        ReleaseAuxiliary(registration, bytes); return true;
    }

    /// <summary>Atomic retained-buffer resize; denial leaves the old lease and all aggregate counters unchanged.</summary>
    public bool TryResizeAuxiliaryMemory(string processId, long leaseId, long bytes)
    {
        if (bytes < 0 || !_processes.TryGetValue(processId, out var registration) || !registration.AuxiliaryLeases.TryGetValue(leaseId, out var old)) return false;
        if (bytes == 0) return TryReleaseAuxiliaryMemory(processId, leaseId);
        var increase = bytes - old;
        var principal = registration.Device.Principal;
        if (increase > 0)
        {
            if (_disabled || registration.Cancelled || IsTerminal(registration.Process) || _controls.ContainsKey(processId) ||
                _revokedArtifacts.Contains(registration.Process.ArtifactDigest) ||
                increase > _continuationPolicy.AuxiliaryBytesPerProcess - registration.AuxiliaryReserved ||
                increase > _continuationPolicy.AuxiliaryBytesPerPrincipal - principal.AuxiliaryReserved ||
                increase > _continuationPolicy.AuxiliaryBytes - _auxiliaryReserved || increase > _limits.ReservedMemoryBytes - _reserved ||
                increase > _limits.PrincipalReservedMemoryBytes - principal.Reserved) return false;
            if (_hardware.TryGetValue(registration.Device.Id, out var physical) && increase > physical.Quota.MemoryBytes - physical.Reserved) return false;
        }
        registration.AuxiliaryLeases[leaseId] = bytes;
        ReleaseAuxiliary(registration, -increase); return true;
    }

    private void ReleaseAuxiliary(Registration registration, long bytes)
    {
        registration.AuxiliaryReserved -= bytes; registration.Device.Principal.AuxiliaryReserved -= bytes;
        _auxiliaryReserved -= bytes; registration.Device.Principal.Reserved -= bytes; _reserved -= bytes;
        if (_hardware.TryGetValue(registration.Device.Id, out var physical)) physical.Reserved -= bytes;
    }

    private void CancelContinuations(Registration registration, MetaStopReason reason)
    {
        if (registration.Cancelled) return;
        registration.Cancelled = true; registration.ContinuationPending = false; registration.InvocationActive = false;
        try { registration.Continuations?.Cancel(registration.Id, reason); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { /* Stop still detaches the source and releases every reservation. */ }
        finally { registration.AuxiliaryLeases.Clear(); ReleaseAuxiliary(registration, registration.AuxiliaryReserved); }
    }

    private static bool IsTerminal(MetaProcess process) => process.State is MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined;
    private static bool HasWork(Registration registration) => !IsTerminal(registration.Process) &&
        (registration.Process.State is MetaProcessState.Runnable or MetaProcessState.Collecting || registration.ContinuationPending ||
         registration.Process.State == MetaProcessState.Idle && !registration.InvocationActive && registration.Process.QueuedEvents > 0);

    public bool TryEnqueueEvent(string processId, int watchIndex, IReadOnlyList<MetaValue> arguments, long sequence, ulong authorityToken = 0, ulong contextToken = 0)
    {
        if (!_processes.TryGetValue(processId, out var registration)) return false;
        var accepted = registration.Process.TryEnqueueEvent(watchIndex, arguments, sequence, authorityToken, contextToken);
        if (accepted && registration.Continuations == null && registration.Process.State == MetaProcessState.Idle) registration.Process.TryStartNextEvent();
        NotifyRunnable(processId); return accepted;
    }

    public bool QueueStop(string processId, MetaStopReason reason = MetaStopReason.Stop)
    {
        if (!_processes.ContainsKey(processId)) return false;
        _controls[processId] = reason; return true;
    }
    public bool QueueCompletion(string processId, long operationId, MetaValue value)
    {
        if (!_processes.TryGetValue(processId, out var registration) || registration.Process.State != MetaProcessState.Waiting || registration.Process.PendingOperationId != operationId || _completions.ContainsKey(processId)) return false;
        _completions.Add(processId, (operationId, value)); return true;
    }
    public bool TryScheduleWake(string processId, long atMs)
    {
        if (!_processes.ContainsKey(processId) || atMs < _now || _timers.Count >= _limits.MaximumTimers) return false;
        _timers.Enqueue(() => NotifyRunnable(processId), atMs); return true;
    }
    public void RevokeArtifact(string digest)
    {
        _revokedArtifacts.Add(digest);
        // Revocation is a rare explicit control action, rather than an every-tick installation scan.
        foreach (var registration in _processes.Values) if (registration.Process.ArtifactDigest == digest) _controls[registration.Id] = MetaStopReason.ArtifactRevoked;
    }
    public void StopPrincipal(string principalId, MetaStopReason reason = MetaStopReason.Stop)
    {
        foreach (var registration in _processes.Values) if (registration.Device.Principal.Id == principalId) _controls[registration.Id] = reason;
    }
    public void StopDevice(string principalId, string deviceId, MetaStopReason reason = MetaStopReason.Stop)
    {
        foreach (var registration in _processes.Values) if (registration.Device.Principal.Id == principalId && registration.Device.Id == deviceId) _controls[registration.Id] = reason;
    }
    public void StopAll()
    {
        _disabled = true;
        foreach (var registration in _processes.Values) _controls[registration.Id] = MetaStopReason.Stop;
    }

    public MetaSchedulerTickResult RunTick(long nowMs, int gas, TimeSpan? wallLimit = null, Action? commitPreviousIntents = null, Action? boundedAdmission = null, Action? publishDeltas = null, Func<string, bool>? beforeSlice = null)
    {
        if (_runningTick) throw new InvalidOperationException("Scheduler ticks cannot be reentered.");
        if (nowMs < _now || gas < 0 || wallLimit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(nowMs));
        if (_requiresTickIdentity && (!_clockSet || _lastRun == (_clockEpoch, _clockTick)))
            throw new InvalidOperationException("Continuations require one RunTick per explicit epoch/tick.");
        if (_requiresTickIdentity && publishDeltas != null)
            throw new ArgumentException("Continuation publication must use charged native steps.", nameof(publishDeltas));
        _runningTick = true;
        try { return RunTickCore(nowMs, gas, wallLimit, commitPreviousIntents, boundedAdmission, publishDeltas, beforeSlice); }
        finally { _runningTick = false; }
    }

    private MetaSchedulerTickResult RunTickCore(long nowMs, int gas, TimeSpan? wallLimit, Action? commitPreviousIntents,
        Action? boundedAdmission, Action? publishDeltas, Func<string, bool>? beforeSlice)
    {
        using var metric = MetaMetricScope.Measure(Metrics, MetaMetric.SchedulerElapsed, _clockTick);
        _lastRun = _clockSet ? (_clockEpoch, _clockTick) : null;
        _now = nowMs;
        var started = _time.GetTimestamp(); var controls = 0; var completions = 0; var used = 0; var slices = 0;
        _tickStarted = started; _tickWallLimit = wallLimit;
        var nativeGas = 0; var nativeSteps = 0;
        // A stopped process never needs event/native queue capacity to lose authority.
        if (_controls.Count != 0)
            foreach (var control in _controls.ToArray())
                if (_processes.TryGetValue(control.Key, out var target)) ApplyControl(target, ref controls);
        // A failed value can terminate a process; its trusted cancellation may remove another registration.
        // Drain a bounded snapshot and remove each exact pending slot before invoking process callbacks.
        foreach (var completion in _completions.Count == 0 ? [] : _completions.ToArray())
        {
            if (!_completions.Remove(completion.Key, out var current) || !_processes.TryGetValue(completion.Key, out var registration) || ApplyControl(registration, ref controls)) continue;
            if (registration.Process.TryCompleteHost(current.Operation, current.Value)) { completions++; NotifyRunnable(completion.Key); }
        }
        foreach (var registration in _deferred) { registration.Deferred = false; NotifyRunnable(registration.Id); }
        _deferred.Clear();
        if (!_requiresTickIdentity || HasTime(started, wallLimit)) commitPreviousIntents?.Invoke();
        if (!_requiresTickIdentity || HasTime(started, wallLimit)) boundedAdmission?.Invoke();
        var due = 0;
        while (due++ < _limits.MaximumDispatches && _timers.TryPeek(out _, out var time) && time <= nowMs)
        {
            if (_requiresTickIdentity && !HasTime(started, wallLimit)) break;
            _timers.Dequeue()();
        }
        for (var dispatch = 0; dispatch < _limits.MaximumDispatches && used < gas && _ready.TryDequeue(out var principal); dispatch++)
        {
            principal.Enqueued = false;
            if (principal.Removed) continue;
            if (!HasTime(started, wallLimit)) { Enqueue(principal); break; }
            principal.Window.Refresh(nowMs, _limits.WindowMilliseconds);
            if (principal.Window.Gas >= _limits.PrincipalGasPerWindow) { Park(principal); continue; }
            if (!principal.Ready.TryDequeue(out var device)) continue;
            device.Enqueued = false; device.Window.Refresh(nowMs, _limits.WindowMilliseconds);
            if (device.Removed) { Enqueue(principal); continue; }
            _hardware.TryGetValue(device.Id, out var physical);
            physical?.Window.Refresh(nowMs, _limits.WindowMilliseconds);
            var hardwareGas = physical == null ? _limits.DeviceGasPerWindow : physical.Quota.GasPerWindow * physical.Throttle / 1000;
            if (physical != null && physical.Window.Gas >= hardwareGas) { Park(device); Enqueue(principal); continue; }
            if (device.Window.Gas >= _limits.DeviceGasPerWindow) { Park(device); Enqueue(principal); continue; }
            if (!device.Ready.TryDequeue(out var registration)) { Enqueue(principal); continue; }
            registration.Enqueued = false; registration.Window.Refresh(nowMs, _limits.WindowMilliseconds);
            try
            {
                if (registration.Removed || ApplyControl(registration, ref controls) || IsTerminal(registration.Process)) continue;
                if (registration.Window.Gas >= _limits.ProcessGasPerWindow) { Park(registration); continue; }
                if (registration.Process.DebugPaused) { ScheduleDeadline(registration); continue; }
                // Controls queued by another continuation are observed before lookup, proof and work.
                if (beforeSlice != null)
                {
                    var stop = MetaStopReason.None;
                    try { if (!beforeSlice(registration.Id)) stop = MetaStopReason.PermissionRevoked; }
                    catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { stop = MetaStopReason.InternalFault; }
                    if (stop != MetaStopReason.None) { registration.Process.Stop(stop); controls++; continue; }
                    if (ApplyControl(registration, ref controls)) continue;
                }
                var hasNative = TryPeekContinuation(registration, out var step, ref controls);
                if (IsTerminal(registration.Process)) continue;
                if (hasNative && registration.PreferGuest && registration.Process.State == MetaProcessState.Runnable) hasNative = false;
                // Finishing an app result always precedes a watch that could clear LastResult.
                if (!registration.InvocationActive && registration.Process.State == MetaProcessState.Idle && registration.Process.QueuedEvents > 0 &&
                    (!hasNative || registration.PreferWatch))
                {
                    registration.Process.TryStartNextEvent(); registration.PreferWatch = false; hasNative = false;
                }
                if (!hasNative && registration.Process.State is not (MetaProcessState.Runnable or MetaProcessState.Collecting)) continue;
                principal.Deficit = Math.Min(_limits.MaximumDebt, principal.Deficit + _limits.Quantum * principal.Weight);
                device.Deficit = Math.Min(_limits.MaximumDebt, device.Deficit + _limits.Quantum * device.Weight);
                registration.Deficit = Math.Min(_limits.MaximumDebt, registration.Deficit + _limits.Quantum * registration.Weight);
                var grant = (int) Math.Min(Math.Min(_limits.SliceGas, gas - used), Math.Min(principal.Deficit, Math.Min(device.Deficit, registration.Deficit)));
                grant = (int) Math.Min(grant, Math.Min(_limits.PrincipalGasPerWindow - principal.Window.Gas, Math.Min(_limits.DeviceGasPerWindow - device.Window.Gas, _limits.ProcessGasPerWindow - registration.Window.Gas)));
                if (physical != null) grant = (int) Math.Min(grant, Math.Min(physical.Quota.MaxBurstGas, hardwareGas - physical.Window.Gas));
                if (grant <= 0 || !HasTime(started, wallLimit)) continue;
                if (_clockSet) registration.Process.SetClock(_clockEpoch, _clockTick, _clockPeriod);
                if (hasNative)
                {
                    var cost = Math.Max(_continuationPolicy.MinimumStepGas, step.GasCost);
                    if (cost > _limits.PrincipalGasPerWindow || cost > _limits.DeviceGasPerWindow || cost > _limits.ProcessGasPerWindow ||
                        physical != null && (cost > physical.Quota.MaxBurstGas || cost > physical.Quota.GasPerWindow))
                    { registration.Process.Stop(MetaStopReason.InternalFault); controls++; continue; }
                    if (cost > gas - used) { Defer(registration); continue; }
                    if (cost > _limits.PrincipalGasPerWindow - principal.Window.Gas) { Park(principal); continue; }
                    if (cost > _limits.DeviceGasPerWindow - device.Window.Gas || physical != null && cost > hardwareGas - physical.Window.Gas) { Park(device); continue; }
                    if (cost > _limits.ProcessGasPerWindow - registration.Window.Gas) { Park(registration); continue; }
                    // Known native work never overruns global/window ceilings. Slice debt remains bounded.
                    Charge(registration, physical, cost); used = checked(used + cost); nativeGas = checked(nativeGas + cost); nativeSteps++;
                    ExecuteContinuation(registration, step, ref controls);
                    registration.PreferGuest = true;
                    if (registration.Process.State == MetaProcessState.Idle) registration.PreferWatch = true;
                    ApplyControl(registration, ref controls);
                }
                else
                {
                    var yieldVersion = registration.Process.YieldVersion;
                    var remainingWall = wallLimit is { } limit ? limit - _time.GetElapsedTime(started) : (TimeSpan?) null;
                    var result = registration.Process.RunSlice(grant, nowMs, 4096, remainingWall);
                    used = checked(used + result.GasUsed); slices++;
                    Charge(registration, physical, result.GasUsed);
                    registration.PreferGuest = false;
                    if (registration.Continuations != null && registration.Process.YieldVersion != yieldVersion) Defer(registration);
                }
                ScheduleDeadline(registration);
                if (registration.Process.State == MetaProcessState.Idle)
                {
                    if (registration.Continuations == null) registration.Process.TryStartNextEvent();
                    else registration.ContinuationPending = true;
                }
            }
            finally { NotifyRunnable(registration.Id); Enqueue(device); Enqueue(principal); }
        }
        // Legacy no-source callers retain their existing tail callback behavior.
        publishDeltas?.Invoke();
        return new MetaSchedulerTickResult(used, slices, controls, completions, _ready.Count)
        {
            GuestGasUsed = used - nativeGas, ContinuationGasUsed = nativeGas, ContinuationSteps = nativeSteps,
            WallExhausted = !HasTime(started, wallLimit), GasExhausted = used >= gas
        };
    }

    private bool HasTime(long started, TimeSpan? ceiling) => ceiling == null || _time.GetElapsedTime(started) < ceiling.Value;

    private bool ApplyControl(Registration registration, ref int controls)
    {
        if (!_controls.Remove(registration.Id, out var reason))
        {
            if (_disabled) reason = MetaStopReason.Stop;
            else if (_revokedArtifacts.Contains(registration.Process.ArtifactDigest)) reason = MetaStopReason.ArtifactRevoked;
            else return false;
        }
        _completions.Remove(registration.Id);
        if (!IsTerminal(registration.Process)) { registration.Process.Stop(reason); controls++; }
        return true;
    }

    private bool TryPeekContinuation(Registration registration, out MetaContinuationStep step, ref int controls)
    {
        step = default;
        if (registration.Continuations == null || !registration.ContinuationPending) return false;
        try
        {
            if (!registration.Continuations.TryPeek(registration.Id, out step))
            {
                registration.ContinuationPending = false;
                if (registration.InvocationActive && registration.Process.State == MetaProcessState.Idle)
                    throw new InvalidOperationException("A returned invocation requires a completion step.");
                return false;
            }
            if (step.Ticket <= 0 || !Enum.IsDefined(step.Kind) || step.GasCost < 0 ||
                step.GasCost > _continuationPolicy.MaximumStepGas || Math.Max(step.GasCost, _continuationPolicy.MinimumStepGas) > _limits.MaximumDebt)
                throw new InvalidOperationException("Invalid trusted native work quote.");
            var eligible = step.Kind switch
            {
                MetaContinuationKind.PrepareInvocation => registration.Process.State == MetaProcessState.Idle && !registration.InvocationActive,
                MetaContinuationKind.CompleteInvocation => registration.Process.State == MetaProcessState.Idle && registration.InvocationActive,
                _ => registration.Process.State != MetaProcessState.Collecting
            };
            if (!eligible) registration.ContinuationPending = false;
            return eligible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { registration.Process.Stop(MetaStopReason.InternalFault); controls++; return false; }
    }

    private void ExecuteContinuation(Registration registration, MetaContinuationStep step, ref int controls)
    {
        var before = registration.Process.State;
        try
        {
            var result = registration.Continuations!.Execute(registration.Id, registration.Process, step);
            if (IsTerminal(registration.Process)) return;
            if (result == MetaContinuationResult.InvocationStarted && step.Kind == MetaContinuationKind.PrepareInvocation &&
                before == MetaProcessState.Idle && registration.Process.State == MetaProcessState.Runnable && !registration.InvocationActive)
            { registration.InvocationActive = true; registration.PreferWatch = true; }
            else if (result is MetaContinuationResult.InvocationCompleted or MetaContinuationResult.Rejected &&
                     step.Kind == MetaContinuationKind.CompleteInvocation && before == MetaProcessState.Idle && registration.Process.State == before)
                registration.InvocationActive = false;
            else if (result is MetaContinuationResult.InvocationStarted or MetaContinuationResult.InvocationCompleted || registration.Process.State != before || !Enum.IsDefined(result))
                throw new InvalidOperationException("Invalid native continuation state transition.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { registration.Process.Stop(MetaStopReason.InternalFault); controls++; }
    }

    private void Defer(Registration registration)
    {
        if (registration.Deferred) return;
        registration.Deferred = true; _deferred.Add(registration);
    }

    private void Charge(Registration registration, Hardware? physical, int gas)
    {
        var device = registration.Device; var principal = device.Principal;
        principal.Deficit = Math.Max(-_limits.MaximumDebt, principal.Deficit - gas);
        device.Deficit = Math.Max(-_limits.MaximumDebt, device.Deficit - gas);
        registration.Deficit = Math.Max(-_limits.MaximumDebt, registration.Deficit - gas);
        principal.Window.Gas += gas; device.Window.Gas += gas; registration.Window.Gas += gas;
        if (physical != null) { physical.Window.Gas += gas; physical.TotalGas = physical.TotalGas > long.MaxValue - gas ? long.MaxValue : physical.TotalGas + gas; }
    }

    private void Enqueue(Device device)
    {
        if (device.Removed || device.Enqueued || device.Parked || device.Ready.Count == 0) return;
        device.Enqueued = true; device.Principal.Ready.Enqueue(device); Enqueue(device.Principal);
    }
    private static void RemoveFromQueue<T>(Queue<T> queue, T removed) where T : class
    {
        var count = queue.Count;
        for (var index = 0; index < count; index++) { var current = queue.Dequeue(); if (!ReferenceEquals(current, removed)) queue.Enqueue(current); }
    }
    private void Enqueue(Principal principal)
    {
        if (principal.Removed || principal.Enqueued || principal.Parked || principal.Ready.Count == 0) return;
        principal.Enqueued = true; _ready.Enqueue(principal);
    }
    private void Park(Principal principal)
    {
        if (principal.Parked) return;
        principal.Parked = true;
        _timers.Enqueue(() => { principal.Parked = false; Enqueue(principal); }, principal.Window.Start + _limits.WindowMilliseconds);
    }
    private void Park(Device device)
    {
        if (device.Parked) return;
        device.Parked = true;
        _timers.Enqueue(() => { device.Parked = false; Enqueue(device); }, device.Window.Start + _limits.WindowMilliseconds);
    }
    private void Park(Registration registration)
    {
        if (registration.Parked) return;
        registration.Parked = true;
        _timers.Enqueue(() => { registration.Parked = false; NotifyRunnable(registration.Id); }, registration.Window.Start + _limits.WindowMilliseconds);
    }
    private void ScheduleDeadline(Registration registration)
    {
        var deadlineValue = registration.Process.DebugPauseDeadlineMs is { } pause ? Math.Min(pause, registration.Process.ActivationDeadlineMs ?? long.MaxValue) : registration.Process.ActivationDeadlineMs;
        if (registration.Removed || deadlineValue is not { } deadline || registration.Process.State is MetaProcessState.Idle or MetaProcessState.Stopped or MetaProcessState.Faulted or MetaProcessState.Quarantined) return;
        if (registration.DeadlineScheduled)
        {
            if (registration.DeadlineAt <= deadline) return;
            _timers.Remove(registration.DeadlineCallback!, out _, out _);
        }
        registration.DeadlineScheduled = true;
        registration.DeadlineAt = deadline;
        registration.DeadlineCallback = () =>
        {
            registration.DeadlineScheduled = false;
            if (registration.Removed) return;
            registration.Process.CheckDeadline(_now);
            ScheduleDeadline(registration);
        };
        _timers.Enqueue(registration.DeadlineCallback, deadline == long.MaxValue ? deadline : deadline + 1);
    }
}
