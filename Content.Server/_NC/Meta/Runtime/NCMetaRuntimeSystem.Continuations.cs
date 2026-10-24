// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Content.Meta.Runtime;

namespace Content.Server._NC.Meta.Runtime;

/// <summary>Trusted application work over one existing registration, never a client-supplied execution delegate.</summary>
public interface INCMetaRuntimeApplicationWork
{
    bool TryPeek(out MetaContinuationStep step);
    MetaContinuationResult Execute(NCMetaRuntimeInstance instance, in MetaContinuationStep step);
    void Cancel(MetaStopReason reason);
}

public sealed partial class NCMetaRuntimeSystem
{
    private sealed class RuntimeWork(NCMetaRuntimeSystem system, NCMetaRuntimeInstance instance) : IMetaSchedulerContinuations
    {
        public INCMetaRuntimeApplicationWork? Application;
        public Intent? PendingIntent;
        public NativeCompletion? Completion;
        public bool Dirty;
        public bool Cancelled;
        public LegacyInvocation? Legacy;
        public bool TryPeek(string processId, out MetaContinuationStep step) => system.TryPeekWork(instance, this, out step);
        public MetaContinuationResult Execute(string processId, MetaProcess process, in MetaContinuationStep step) => system.ExecuteWork(instance, this, step);
        public void Cancel(string processId, MetaStopReason reason)
        {
            if (Cancelled) return;
            Cancelled = true;
            try { Application?.Cancel(reason); }
            finally
            {
                if (PendingIntent is { } pending) system.FinishIntent(pending);
                if (Legacy is { } legacy) system._scheduler.TryReleaseAuxiliaryMemory(instance.ProcessId, legacy.MemoryLease);
                Legacy = null;
                Completion = null; system.PruneCancelledWork();
                Application = null;
            }
        }
    }
    private sealed class LegacyInvocation(EntityUid actor, int function, Func<MetaProcess, IReadOnlyList<MetaValue>> arguments,
        Action<MetaProcess, MetaValue?> completed, Func<bool> current, INCMetaDeferredInvocationAuthority? authority, long memoryLease)
    {
        public readonly EntityUid Actor = actor;
        public readonly int Function = function;
        public readonly Func<MetaProcess, IReadOnlyList<MetaValue>> Arguments = arguments;
        public readonly Action<MetaProcess, MetaValue?> Completed = completed;
        public readonly Func<bool> Current = current;
        public readonly INCMetaDeferredInvocationAuthority? Authority = authority;
        public readonly long MemoryLease = memoryLease;
        public bool Started;
    }
    private sealed record NativeCompletion(Intent Intent, Action Apply);
    private readonly Dictionary<string, RuntimeWork> _runtimeWork = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Intent> _pendingNative = [];
    private readonly object _completionLock = new();
    private int _committedThisTick;

    public bool TryBindApplicationWork(EntityUid host, Guid generation, INCMetaRuntimeApplicationWork source)
    {
        var id = generation.ToString("N");
        if (!_processes.TryGetValue(id, out var runtime) || runtime.Host != host || !IsCurrent(runtime) ||
            !_runtimeWork.TryGetValue(id, out var work) || work.Cancelled) return false;
        if (work.Application != null && !ReferenceEquals(work.Application, source)) work.Application.Cancel(MetaStopReason.Stop);
        work.Application = source;
        return _scheduler.NotifyContinuation(id);
    }

    public bool TryWakeApplicationWork(EntityUid host, Guid generation, bool nextTick = false)
    {
        var id = generation.ToString("N");
        return _processes.TryGetValue(id, out var runtime) && runtime.Host == host && IsCurrent(runtime) &&
            (nextTick ? _scheduler.NotifyContinuationNextTick(id) : _scheduler.NotifyContinuation(id));
    }

    public bool TryReserveApplicationMemory(EntityUid host, Guid generation, long bytes, out long lease)
    {
        lease = 0; var id = generation.ToString("N");
        return _processes.TryGetValue(id, out var runtime) && runtime.Host == host && IsCurrent(runtime) &&
            _scheduler.TryReserveAuxiliaryMemory(id, bytes, out lease);
    }
    public bool TryReleaseApplicationMemory(Guid generation, long lease) => _scheduler.TryReleaseAuxiliaryMemory(generation.ToString("N"), lease);
    public bool TryResizeApplicationMemory(EntityUid host, Guid generation, long lease, long bytes)
    {
        var id = generation.ToString("N");
        return _processes.TryGetValue(id, out var runtime) && runtime.Host == host && IsCurrent(runtime) &&
            _scheduler.TryResizeAuxiliaryMemory(id, lease, bytes);
    }

    public bool TryStartApplicationInvocation(NCMetaRuntimeInstance runtime, EntityUid actor, int function,
        Func<MetaProcess, IReadOnlyList<MetaValue>> arguments, INCMetaDeferredInvocationAuthority authority)
    {
        if (!IsCurrent(runtime) || !IsDeferredContextCurrent(authority) || runtime.Process.State != MetaProcessState.Idle ||
            _invocations.Contains(runtime) || _invocations.Count >= 64 || !runtime.Process.TryStartHostInvocation(function, arguments)) return false;
        runtime.InvocationActor = actor;
        runtime.InvocationAuthorization = authority.IsCurrent;
        runtime.InvocationDeferredAuthority = authority;
        _invocations.Add(runtime);
        return true;
    }

    /// <summary>Detach exactly the old actor proof before its native completion/projection callback can enqueue work.</summary>
    public void DetachApplicationInvocation(NCMetaRuntimeInstance runtime)
    {
        runtime.InvocationActor = null; runtime.InvocationAuthorization = null; runtime.InvocationDeferredAuthority = null;
    }
    public void FinishApplicationInvocation(NCMetaRuntimeInstance runtime)
    {
        DetachApplicationInvocation(runtime); _invocations.Remove(runtime);
    }

    private void StageNativeWakes()
    {
        // Queue-header promotion only: no value construction, callback or world effect in this prefix.
        // Every unit checks the original scheduler deadline; actual work is charged in ordinary HDRR turns.
        var count = Math.Min(32, _intents.Count);
        for (var index = 0; index < count && _scheduler.HasTimeRemaining && _intents.TryDequeue(out var intent); index++)
        {
            if (intent.Cancelled) continue;
            _intents.Enqueue(intent); _scheduler.NotifyContinuation(intent.Runtime.ProcessId);
        }
        for (var index = 0; index < 32 && _scheduler.HasTimeRemaining && _completed.TryDequeue(out var completed); index++)
            if (!completed.Intent.Cancelled && _runtimeWork.TryGetValue(completed.Intent.Runtime.ProcessId, out var source) && !source.Cancelled)
            { source.Completion = completed; _scheduler.NotifyContinuation(completed.Intent.Runtime.ProcessId); }
    }

    private bool TryPeekWork(NCMetaRuntimeInstance runtime, RuntimeWork source, out MetaContinuationStep step)
    {
        step = default;
        if (source.Cancelled) return false;
        if (source.Completion is { } completion)
        { step = new(completion.Intent.Id, MetaContinuationKind.ApplyHostCompletion, QuoteNativeWork(completion.Intent)); return true; }
        if (_committedThisTick < 32 && source.PendingIntent is { Cancelled: false, Committed: false } intent && intent.EnqueuedTick < _timing.CurTick.Value)
        { step = new(intent.Id, MetaContinuationKind.CommitIntent, QuoteNativeWork(intent)); return true; }
        if (source.Legacy is { } legacy)
        { step = new(1, legacy.Started ? MetaContinuationKind.CompleteInvocation : MetaContinuationKind.PrepareInvocation, 512); return true; }
        if (source.Dirty && !_invocations.Contains(runtime)) { step = new(long.MaxValue, MetaContinuationKind.PublishSession, 64); return true; }
        if (source.Application?.TryPeek(out step) == true) return true;
        return false;
    }

    private MetaContinuationResult ExecuteWork(NCMetaRuntimeInstance runtime, RuntimeWork source, MetaContinuationStep step)
    {
        if (!IsCurrent(runtime)) { Stop(runtime, MetaStopReason.PermissionRevoked); return MetaContinuationResult.Rejected; }
        if (step.Kind == MetaContinuationKind.CommitIntent)
        {
            if (source.PendingIntent is not { Cancelled: false, Committed: false } intent || intent.Id != step.Ticket || intent.EnqueuedTick >= _timing.CurTick.Value || _committedThisTick >= 32)
                return MetaContinuationResult.NothingCurrent;
            intent.Committed = true; _committedThisTick++;
            RemoveCommittedIntent(intent); CommitIntent(intent); return MetaContinuationResult.Progress;
        }
        if (step.Kind == MetaContinuationKind.ApplyHostCompletion)
        {
            if (source.Completion is not { } completion || completion.Intent.Id != step.Ticket)
                return MetaContinuationResult.NothingCurrent;
            source.Completion = null;
            if (!completion.Intent.Cancelled)
            {
                if (CanCompleteIntent(completion.Intent)) completion.Apply();
                else { FinishIntent(completion.Intent); Stop(runtime, MetaStopReason.PermissionRevoked); }
            }
            return MetaContinuationResult.Progress;
        }
        if (source.Legacy is { } legacy)
        {
            if (!legacy.Started && step.Kind == MetaContinuationKind.PrepareInvocation)
            {
                if (!legacy.Current() || !IsDeferredContextCurrent(legacy.Authority))
                { source.Legacy = null; _scheduler.TryReleaseAuxiliaryMemory(runtime.ProcessId, legacy.MemoryLease); return MetaContinuationResult.Rejected; }
                if (!_invocations.Contains(runtime) && _invocations.Count < 64 && runtime.Process.TryStartHostInvocation(legacy.Function, legacy.Arguments))
                {
                    runtime.InvocationActor = legacy.Actor; runtime.InvocationAuthorization = legacy.Current;
                    runtime.InvocationDeferredAuthority = legacy.Authority; _invocations.Add(runtime); legacy.Started = true;
                    return MetaContinuationResult.InvocationStarted;
                }
                _scheduler.NotifyContinuationNextTick(runtime.ProcessId); return MetaContinuationResult.NothingCurrent;
            }
            if (legacy.Started && step.Kind == MetaContinuationKind.CompleteInvocation)
            {
                source.Legacy = null; DetachApplicationInvocation(runtime); _invocations.Remove(runtime);
                try { if (legacy.Current() && IsDeferredContextCurrent(legacy.Authority)) legacy.Completed(runtime.Process, runtime.Process.LastResult); }
                finally { _scheduler.TryReleaseAuxiliaryMemory(runtime.ProcessId, legacy.MemoryLease); }
                return MetaContinuationResult.InvocationCompleted;
            }
        }
        if (source.Dirty && step.Kind == MetaContinuationKind.PublishSession && step.Ticket == long.MaxValue)
        {
            source.Dirty = false; ViewStateChanged?.Invoke(runtime.Host);
            return MetaContinuationResult.Progress;
        }
        if (source.Application?.TryPeek(out var current) == true && current == step) return source.Application.Execute(runtime, step);
        return MetaContinuationResult.NothingCurrent;
    }

    // Starting policy quotes include bounded native DTO work plus admitted strings, separately from guest gas.
    // Native adapters keep their own cardinality/byte caps; final calibration uses captured native CPU/elapsed.
    private static int QuoteNativeWork(Intent intent)
    {
        var gas = 256;
        foreach (var argument in intent.Request.Arguments)
            if (argument.Kind == MetaValueKind.String) gas += (intent.Runtime.Process.GetString(argument).Length * 3 + 15) / 16;
        return Math.Min(8192, gas);
    }

    private bool CanCompleteIntent(Intent intent)
    {
        if (intent.Cancelled || intent.Runtime.Process.State != MetaProcessState.Waiting || intent.Runtime.Process.PendingOperationId != intent.Id) return false;
        var import = intent.Runtime.Process.GetImport(intent.Request.ImportId);
        return CanUse(intent.Runtime, intent.Binding, Content.Meta.Language.MetaApiCatalog.Default.Members.Single(member => member.QualifiedName == import.Name));
    }
    private void EnqueueNativeCompletion(Intent intent, Action callback)
    {
        if (intent.Cancelled || Interlocked.CompareExchange(ref intent.CompletionQueued, 1, 0) != 0) return;
        lock (_completionLock)
            if (!intent.Cancelled) _completed.Enqueue(new(intent, callback));
    }
    private void FinishIntent(Intent intent)
    {
        if (intent.Cancelled) return;
        intent.Cancelled = true; _pendingNative.Remove(intent.Id);
        if (_runtimeWork.TryGetValue(intent.Runtime.ProcessId, out var source) && ReferenceEquals(source.PendingIntent, intent)) source.PendingIntent = null;
        _scheduler.TryReleaseAuxiliaryMemory(intent.Runtime.ProcessId, intent.MemoryLease);
    }
    private void RemoveCommittedIntent(Intent removed)
    {
        var count = _intents.Count;
        for (var index = 0; index < count && _intents.TryDequeue(out var item); index++)
            if (!ReferenceEquals(item, removed) && !item.Cancelled) _intents.Enqueue(item);
    }
    private void PruneCancelledWork()
    {
        var count = _intents.Count;
        for (var index = 0; index < count && _intents.TryDequeue(out var intent); index++) if (!intent.Cancelled) _intents.Enqueue(intent);
        lock (_completionLock)
        {
            count = _completed.Count;
            for (var index = 0; index < count && _completed.TryDequeue(out var completion); index++) if (!completion.Intent.Cancelled) _completed.Enqueue(completion);
        }
    }
}
