// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Applications;

public sealed partial class NCMetaApplicationSystem
{
    private sealed class PendingProjection(NCMetaApplicationSystem system, Session session, MuiProjectionResult result,
        long version, long lease, long bytes) : IDisposable
    {
        public readonly MuiProjectionResult Result = result;
        public readonly long Version = version;
        public readonly long Lease = lease;
        public long Bytes = bytes;
        public MuiPatchWriter? Patch;
        public bool Resize(long value)
        {
            if (value <= Bytes) return true;
            if (!system._runtime.TryResizeApplicationMemory(session.Registration.Host, session.Registration.RuntimeGeneration, Lease, value)) return false;
            Bytes = value; return true;
        }
        public void Dispose() { Patch?.Dispose(); system._runtime.TryReleaseApplicationMemory(session.Registration.RuntimeGeneration, Lease); }
    }

    private sealed class ViewAuthority(NCMetaApplicationSystem system, Guid session) : INCMetaDeferredInvocationAuthority
    {
        public bool IsCurrent() => system._sessions.TryGetValue(session, out var current) && system.CanUse(current);
    }

    private sealed class Activation(Session session, bool action, long version, long lease, long bytes) : IDisposable
    {
        public readonly Session Session = session;
        public readonly bool Action = action;
        public readonly long Version = version;
        public long Lease = lease;
        public long Bytes = bytes;
        public bool Started;
        public bool Detached;
        public MuiInputReader? Input;
        public MuiProjectionWriter? Projection;
        public void Dispose() { Input?.Dispose(); Projection?.Dispose(); }
    }

    private sealed class ApplicationWork : INCMetaRuntimeApplicationWork
    {
        private enum Job : byte { None, DiscardResult, Ingress, Control, BeginAction, BeginView, Input, Start, CompleteAction, BeginProjection, Projection, Patch, Publish, Sweep }
        private readonly NCMetaApplicationSystem _system;
        public readonly NCMetaAppRegistration Registration;
        private readonly NCMetaRuntimeInstance _runtime;
        private readonly long _packageLease;
        private readonly LinkedList<Session> _members = [];
        private readonly LinkedList<Session> _ingress = [];
        private readonly LinkedList<Session> _actions = [];
        private readonly LinkedList<Session> _controls = [];
        private readonly SortedSet<(long At, long Order, Guid Id)> _views = [];
        private readonly SortedSet<(long At, long Order, Guid Id)> _outputs = [];
        private LinkedListNode<Session>? _sweep;
        private Activation? _active;
        private long _nextSweep;
        private long _order;
        private long _ticket = 1;
        private int _actionBurst;
        private bool _cancelled;
        private bool _discardResult;
        private readonly Func<MetaProcess, IReadOnlyList<MetaValue>> _arguments;

        public ApplicationWork(NCMetaApplicationSystem system, NCMetaAppRegistration registration, NCMetaRuntimeInstance runtime, long packageLease)
        { _system = system; Registration = registration; _runtime = runtime; _packageLease = packageLease; _arguments = BuildArguments; }

        public bool HasScheduledWork => !_cancelled && (_discardResult || _active != null || _ingress.Count != 0 || _controls.Count != 0 || _actions.Count != 0 ||
            _views.Count != 0 && _views.Min.At <= _system._now || _outputs.Count != 0 && _outputs.Min.At <= _system._now ||
            _members.Count != 0 && _nextSweep <= _system._now);

        public void Add(Session session) { session.MemberNode = _members.AddLast(session); _sweep ??= session.MemberNode; }
        public void Dirty(Session session)
        {
            session.NeedsView = true; session.ViewVersion++;
            QueueView(session); Wake();
        }
        private void QueueView(Session session)
        {
            if (session.ViewKey != null) return;
            session.ViewKey = (session.NextPublish, ++_order, session.Id); _views.Add(session.ViewKey.Value);
        }
        public void EnqueueIngress(Session session)
        { session.IngressNode ??= _ingress.AddLast(session); Wake(); }
        public void EnqueueControl(Session session)
        { session.ControlNode ??= _controls.AddLast(session); Wake(); }
        private void Wake() => _system._runtime.TryWakeApplicationWork(Registration.Host, Registration.RuntimeGeneration);

        public void Remove(Session session)
        {
            if (session.MemberNode is { } member)
            {
                if (_sweep == member) _sweep = member.Next ?? _members.First;
                _members.Remove(member); if (_members.Count == 0) _sweep = null;
                else if (_sweep == member) _sweep = _members.First;
                session.MemberNode = null;
            }
            if (session.IngressNode is { } ingress) _ingress.Remove(ingress);
            if (session.ActionNode is { } action) _actions.Remove(action);
            if (session.ControlNode is { } control) _controls.Remove(control);
            if (session.ViewKey is { } view) _views.Remove(view);
            if (session.OutputKey is { } output) _outputs.Remove(output);
            session.IngressNode = null; session.ActionNode = null; session.ControlNode = null; session.ViewKey = null; session.OutputKey = null;
            if (_active?.Session == session)
            {
                if (!_active.Started) ReleaseActive();
                else if (_runtime.Process.State == MetaProcessState.Idle)
                { _discardResult = true; _system._runtime.FinishApplicationInvocation(_runtime); ReleaseActive(); }
                // A running/waiting frame keeps its revoked proof until beforeSlice stops it; never clear it here.
            }
        }

        private static int InputGas(Session session, string body)
        {
            var name = session.Ingress?.Action ?? session.Action?.Action ?? "";
            var fields = session.Registration.Application.Contract.Actions.TryGetValue(name, out var action) ? action.Inputs.Count : 0;
            return 64 + Math.Min(MuiLimits.Nodes, body.Length * 4 + fields * 4) + (body.Length * 3 + 15) / 16;
        }

        private (Job Job, Session? Session, MetaContinuationKind Kind, int Gas) Select()
        {
            if (_cancelled) return default;
            if (_discardResult) return (Job.DiscardResult, null, MetaContinuationKind.CompleteInvocation, 32);
            if (_active is { } active)
            {
                if (!active.Started)
                {
                    if (active.Action && active.Input == null) return (Job.BeginAction, active.Session, MetaContinuationKind.PrepareInvocation, InputGas(active.Session, active.Session.Action!.Body));
                    if (active.Input?.Progress == MuiCodecProgress.Working) return (Job.Input, active.Session, MetaContinuationKind.PrepareInvocation, active.Input.NextGas);
                    return (Job.Start, active.Session, MetaContinuationKind.PrepareInvocation, 64);
                }
                if (_runtime.Process.State == MetaProcessState.Idle)
                {
                    if (active.Action) return (Job.CompleteAction, active.Session, MetaContinuationKind.CompleteInvocation, 128 + _members.Count * 4);
                    return active.Projection == null ? (Job.BeginProjection, active.Session, MetaContinuationKind.CompleteInvocation, 32) :
                        (Job.Projection, active.Session, MetaContinuationKind.CompleteInvocation, active.Projection.NextGas);
                }
            }
            if (_controls.First is { } control) return (Job.Control, control.Value, MetaContinuationKind.ValidateIngress, ControlGas(control.Value));
            if (_ingress.First is { } ingress) return (Job.Ingress, ingress.Value, MetaContinuationKind.ValidateIngress, InputGas(ingress.Value, ingress.Value.Ingress!.Body));
            if (_outputs.Count > 0 && _outputs.Min.At <= _system._now && _system._sessions.TryGetValue(_outputs.Min.Id, out var output))
                return output.Output?.Patch is { Progress: MuiCodecProgress.Complete } ? (Job.Publish, output, MetaContinuationKind.PublishSession, PublishGas(output)) :
                    (Job.Patch, output, MetaContinuationKind.PublishSession, output.Output?.Patch?.NextGas ?? 64);
            if (_active == null)
            {
                var viewReady = _views.Count > 0 && _views.Min.At <= _system._now;
                if (_actions.First is { } action && (_actionBurst < 2 || !viewReady))
                    return (Job.BeginAction, action.Value, MetaContinuationKind.PrepareInvocation, InputGas(action.Value, action.Value.Action!.Body));
                if (viewReady && _system._sessions.TryGetValue(_views.Min.Id, out var view)) return (Job.BeginView, view, MetaContinuationKind.PrepareInvocation, 64);
            }
            if (_members.Count != 0 && _nextSweep <= _system._now) return (Job.Sweep, (_sweep ?? _members.First)!.Value, MetaContinuationKind.ValidateIngress, 64);
            return default;
        }

        private int PublishGas(Session session)
        {
            var output = session.Output!;
            var package = output.Patch!.FullSnapshot ? Registration.Application.ContractJson.Length + Registration.Application.StyleJson.Length +
                Registration.Application.Views[session.View].Source.Length + Registration.Application.Resources.Sum(value => value.Value.Length + value.Key.Length) : 0;
            return Math.Min(8192, 64 + (package * 3 + output.Patch.Body!.Length * 3 + 63) / 64 + session.Receipts.Count * 2 + session.Fields.Count * 2);
        }
        private int ControlGas(Session session)
        {
            if (session.ResyncPlan is not { } plan || session.ResyncIndex >= plan.Length) return 128;
            var message = plan[session.ResyncIndex];
            var package = message.FullSnapshot ? Registration.Application.ContractJson.Length + Registration.Application.StyleJson.Length +
                Registration.Application.Views[session.View].Source.Length + Registration.Application.Resources.Sum(value => value.Value.Length + value.Key.Length) : 0;
            return Math.Min(8192, 64 + (package * 3 + message.StateJson.Length * 3 + 63) / 64);
        }

        public bool TryPeek(out MetaContinuationStep step)
        {
            var selected = Select(); step = new(_ticket, selected.Kind, selected.Gas);
            return selected.Job != Job.None;
        }

        public MetaContinuationResult Execute(NCMetaRuntimeInstance instance, in MetaContinuationStep step)
        {
            var selected = Select();
            if (_cancelled || !ReferenceEquals(instance, _runtime) || selected.Job == Job.None || step != new MetaContinuationStep(_ticket, selected.Kind, selected.Gas))
                return MetaContinuationResult.NothingCurrent;
            _ticket++;
            if (selected.Job == Job.DiscardResult)
            { _discardResult = false; _system._runtime.FinishApplicationInvocation(instance); return MetaContinuationResult.Rejected; }
            if (!_system._persistence.TryCaptureTime(out _system._epoch, out var now)) return MetaContinuationResult.Rejected;
            _system._now = now.Ticks / TimeSpan.TicksPerMillisecond;
            var session = selected.Session!;
            if (!_system._sessions.ContainsKey(session.Id) || !_system.CanUse(session))
            {
                _system.Close(session.Id, "nc-meta-app-access-lost");
                if (selected.Kind == MetaContinuationKind.CompleteInvocation) { _discardResult = false; return MetaContinuationResult.Rejected; }
                return MetaContinuationResult.Progress;
            }
            if (_active is { Started: false, Action: true } preparing && preparing.Session.Action is { } queued && _system._now > queued.Deadline)
            { FailAction(preparing.Session); return MetaContinuationResult.Rejected; }
            switch (selected.Job)
            {
                case Job.Ingress: AdmitIngress(session); break;
                case Job.Control: ProcessControl(session); break;
                case Job.BeginAction:
                    if (_active == null)
                    {
                        if (session.ActionNode is { } actionNode) _actions.Remove(actionNode); session.ActionNode = null; _actionBurst++;
                        if (_system._now > session.Action!.Deadline) { FailAction(session); break; }
                        if (!_system.CanAct(session, session.Action!.Action)) { _system.Close(session.Id, "nc-meta-app-access-lost"); break; }
                        if (!Begin(session, true)) { FailAction(session); return MetaContinuationResult.Rejected; }
                    }
                    BeginInput(session); break;
                case Job.BeginView:
                    if (session.ViewKey is { } key) _views.Remove(key); session.ViewKey = null; _actionBurst = 0;
                    if (!Begin(session, false)) { _system.Close(session.Id, "nc-meta-error-quota"); return MetaContinuationResult.Rejected; }
                    session.NeedsView = false; break;
                case Job.Input:
                    if (_active!.Input!.Step() == MuiCodecProgress.Invalid) FailAction(session);
                    break;
                case Job.Start: return Start(session);
                case Job.CompleteAction:
                    _system._runtime.DetachApplicationInvocation(instance);
                    session.Action!.Executed = true;
                    session.Action.Success = instance.Process.LastResult is { } result && instance.Process.GetVariantTag(result) == 0;
                    session.Action.Body = "";
                    _system._runtime.FinishApplicationInvocation(instance); ReleaseActive();
                    foreach (var member in _members) Dirty(member);
                    _system.TryResizeSession(session); return MetaContinuationResult.InvocationCompleted;
                case Job.BeginProjection:
                    _system._runtime.DetachApplicationInvocation(instance); _active!.Detached = true;
                    var view = Registration.Application.Views[session.View];
                    if (instance.Process.LastResult is not { } value) { _system.Close(session.Id, "nc-meta-app-invalid-state"); _discardResult = false; return MetaContinuationResult.Rejected; }
                    _active.Projection = new(instance.Process, Registration.Application.Artifact, view.StateTypeId, value, Registration.Application.Contract.Views[session.View].Fields);
                    break;
                case Job.Projection: return Project(session);
                case Job.Patch: PreparePatch(session); break;
                case Job.Publish: Publish(session); break;
                case Job.Sweep:
                    _sweep = session.MemberNode?.Next ?? _members.First;
                    _nextSweep = _system._now + Math.Max(16, 1000 / Math.Max(1, _members.Count));
                    break;
            }
            return MetaContinuationResult.Progress;
        }

        private bool Begin(Session session, bool action)
        {
            if (!_system._runtime.TryReserveApplicationMemory(Registration.Host, Registration.RuntimeGeneration, 8192, out var lease)) return false;
            _active = new(session, action, session.ViewVersion, lease, 8192); session.Busy = true; return true;
        }
        private bool GrowActive(long bytes)
        {
            var active = _active!;
            if (bytes <= active.Bytes) return true;
            if (!_system._runtime.TryResizeApplicationMemory(Registration.Host, Registration.RuntimeGeneration, active.Lease, bytes)) return false;
            active.Bytes = bytes; return true;
        }
        private void BeginInput(Session session)
        {
            if (!GrowActive(8192L + session.Action!.Body.Length * 16L)) { FailAction(session); return; }
            var action = Registration.Application.Actions[session.Action.Action];
            if (!MuiInputReader.TryCreate(_runtime.Process, Registration.Application.Artifact, action.InputTypeId, session.Action.Body,
                Registration.Application.Contract.Actions[session.Action.Action].Inputs, out var reader)) { FailAction(session); return; }
            _active!.Input = reader;
            if (!GrowActive(reader.ReservationBytes)) FailAction(session);
        }
        private MetaContinuationResult Start(Session session)
        {
            var active = _active!; var app = Registration.Application;
            if (active.Action && (!_system.CanAct(session, session.Action!.Action) || active.Input?.Progress != MuiCodecProgress.Complete))
            { FailAction(session); return MetaContinuationResult.Rejected; }
            var authority = active.Action ? (INCMetaDeferredInvocationAuthority) new AppDeferredAuthority(_system, session.Id, session.Action!.Action, session.Action.Id) : new ViewAuthority(_system, session.Id);
            var function = active.Action ? app.Actions[session.Action!.Action].FunctionId : app.Views[session.View].FunctionId;
            if (!_system._runtime.TryStartApplicationInvocation(_runtime, session.User, function, _arguments, authority))
            { _system._runtime.TryWakeApplicationWork(Registration.Host, Registration.RuntimeGeneration, nextTick: true); return MetaContinuationResult.NothingCurrent; }
            active.Started = true; active.Input?.Dispose(); active.Input = null;
            return MetaContinuationResult.InvocationStarted;
        }
        private IReadOnlyList<MetaValue> BuildArguments(MetaProcess process)
        {
            var active = _active!; var context = _system.Context(active.Session, process);
            return active.Action ? [context, active.Input!.Result] : [context];
        }
        private MetaContinuationResult Project(Session session)
        {
            var active = _active!;
            if (!GrowActive(active.Projection!.NextReservationBytes))
            { session.NeedsView = true; QueueView(session); _system._runtime.FinishApplicationInvocation(_runtime); ReleaseActive(); return MetaContinuationResult.InvocationCompleted; }
            var state = active.Projection.Step();
            if (state == MuiCodecProgress.Invalid)
            { _system.Close(session.Id, "nc-meta-app-invalid-state"); _discardResult = false; _system._runtime.FinishApplicationInvocation(_runtime); ReleaseActive(); return MetaContinuationResult.Rejected; }
            if (state != MuiCodecProgress.Complete) return MetaContinuationResult.Progress;
            if (active.Version == session.ViewVersion)
            {
                session.Output?.Dispose();
                var result = active.Projection.Result!;
                session.Output = new(_system, session, result, active.Version, active.Lease, active.Bytes); active.Lease = 0;
                if (session.OutputKey is { } previous) _outputs.Remove(previous);
                session.OutputKey = (session.NextPublish, ++_order, session.Id); _outputs.Add(session.OutputKey.Value);
            }
            else { session.NeedsView = true; QueueView(session); }
            _system._runtime.FinishApplicationInvocation(_runtime); ReleaseActive(); return MetaContinuationResult.InvocationCompleted;
        }
        private void ReleaseActive()
        {
            if (_active is not { } active) return;
            _active = null; active.Session.Busy = false; active.Dispose();
            if (active.Lease != 0) _system._runtime.TryReleaseApplicationMemory(Registration.RuntimeGeneration, active.Lease);
        }
        private void FailAction(Session session)
        {
            if (session.Action is { } action)
            {
                session.Receipts[action.Id] = new(action.Hash, true, false, session.Revision);
                _system.SendActionResult(session, action.Id, false, false, session.Revision); session.Action = null;
            }
            ReleaseActive(); _system.TryResizeSession(session);
        }

        private void AdmitIngress(Session session)
        {
            var request = session.Ingress!;
            _ingress.Remove(session.IngressNode!); session.IngressNode = null;
            session.Ingress = null;
            if (!_system.CanAct(session, request.Action)) { _system.Close(session.Id, "nc-meta-app-access-lost"); return; }
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { ActionId = request.Action, FormJson = request.Body, BaseRevision = request.Revision }))));
            if (session.Receipts.TryGetValue(request.Id, out var receipt))
            {
                _system.SendActionResult(session, request.Id, receipt.Hash == hash && !receipt.Completed, receipt.Hash == hash && receipt.Success, receipt.Revision);
                _system.TryResizeSession(session); return;
            }
            var app = Registration.Application;
            if (session.Action != null || request.Revision != session.Revision ||
                !MuiDtoCodec.CanDecodeInput(app.Artifact, app.Actions[request.Action].InputTypeId, request.Body, app.Contract.Actions[request.Action].Inputs))
            { _system.SendActionResult(session, request.Id, false, false, session.Revision); _system.TryResizeSession(session); return; }
            while (session.ReceiptOrder.Count >= 64) session.Receipts.Remove(session.ReceiptOrder.Dequeue());
            if (!_system.TryResizeSession(session, 1024 + request.Action.Length * 2L + request.Body.Length * 2L))
            { _system.SendActionResult(session, request.Id, false, false, session.Revision); _system.TryResizeSession(session); return; }
            session.ReceiptOrder.Enqueue(request.Id); session.Receipts.Add(request.Id, new(hash, false, false, session.Revision));
            session.Action = new(request.Id, request.Action, request.Body, hash, _system._now + 5000); session.LastActive = _system._now;
            session.ActionNode = _actions.AddLast(session);
            _system.SendActionResult(session, request.Id, true, false, session.Revision);
        }

        private void PreparePatch(Session session)
        {
            var output = session.Output!;
            if (output.Version != session.ViewVersion) { DropOutput(session); return; }
            if (output.Patch == null) output.Patch = new(session.Fields, output.Result, session.Revision < 0 || session.ForceSnapshot);
            if (!output.Resize(8192L + output.Result.Json.Length * 8L + output.Patch.ReservationBytes)) { DelayOutput(session); return; }
            if (output.Patch.Step() == MuiCodecProgress.Invalid) _system.Close(session.Id, "nc-meta-app-invalid-state");
        }
        private void Publish(Session session)
        {
            var output = session.Output!;
            if (output.Version != session.ViewVersion) { DropOutput(session); return; }
            var patch = output.Patch!;
            var consume = session.Action is { Executed: true };
            if (!patch.Changed && !consume && !session.ForceSnapshot) { DropOutput(session); return; }
            var revision = session.Revision + 1;
            var update = _system.BuildState(session, patch.FullSnapshot, patch.Body!, session.Revision, revision);
            var wire = _system.WireBytes(update);
            var retained = 256L + wire + update.StateJson.Length * 2L + NCMetaMuiPresentationBuilder.ValueMemory(update.Values);
            var replacement = output.Result.Json.Length * 2L + output.Result.Fields.Sum(field => 128L + field.Key.Length * 2L + field.Value.Length * 2L);
            if (!_system.TryResizeSession(session, replacement + retained)) { DelayOutput(session); return; }
            session.Json = output.Result.Json; session.Fields = output.Result.Fields; session.Revision = revision;
            session.ForceSnapshot = false; session.NextPublish = _system._now + 100;
            if (session.Replay.Count >= 16) session.ReplayBytes -= session.Replay.Dequeue().Bytes;
            session.Replay.Enqueue(new(update, wire, retained)); session.ReplayBytes += wire;
            while (session.ReplayBytes > 64 * 1024 && session.Replay.TryDequeue(out var discard)) session.ReplayBytes -= discard.Bytes;
            _system.Send(session, update);
            if (consume && session.Action is { } action)
            {
                session.Receipts[action.Id] = new(action.Hash, true, action.Success, revision);
                _system.SendActionResult(session, action.Id, false, action.Success, revision); session.Action = null;
            }
            DropOutput(session); _system.TryResizeSession(session);
        }
        private void DelayOutput(Session session)
        {
            if (session.OutputKey is { } old) _outputs.Remove(old);
            session.OutputKey = (_system._now + 100, ++_order, session.Id); _outputs.Add(session.OutputKey.Value);
        }
        private void DropOutput(Session session)
        {
            if (session.OutputKey is { } key) _outputs.Remove(key); session.OutputKey = null;
            session.Output?.Dispose(); session.Output = null;
        }
        private void ProcessControl(Session session)
        {
            if (session.PendingAck >= 0)
            {
                if (session.PendingAck > session.Revision) { session.PendingAck = -1; return; }
                var acknowledged = session.PendingAck;
                if (session.Replay.TryPeek(out var entry) && entry.State.Revision <= acknowledged)
                { session.ReplayBytes -= session.Replay.Dequeue().Bytes; _system.TryResizeSession(session); return; }
                session.Acknowledged = Math.Max(session.Acknowledged, acknowledged); session.PendingAck = -1; session.LastActive = _system._now;
            }
            if (session.PendingResync is { } requested)
            {
                if (session.ResyncPlan == null)
                {
                    var next = requested + 1; var replay = session.Replay.Where(entry => entry.State.Revision > requested).Select(entry => entry.State).ToArray();
                    session.ResyncPlan = replay.Length > 0 && replay[0].BaseRevision == requested && replay.All(state => state.Revision == next++) ? replay :
                        session.Revision >= 0 ? [_system.BuildState(session, true, session.Json, session.Revision, session.Revision)] : [];
                    return;
                }
                if (session.ResyncIndex < session.ResyncPlan.Length) { _system.Send(session, session.ResyncPlan[session.ResyncIndex++]); return; }
                session.PendingResync = null; session.ResyncPlan = null; session.ResyncIndex = 0;
            }
            if (session.ControlNode is { } node) _controls.Remove(node); session.ControlNode = null;
        }
        public void Cancel(MetaStopReason reason)
        {
            if (_cancelled) return; _cancelled = true;
            ReleaseActive();
            foreach (var member in _members.ToArray()) _system.Close(member.Id, "nc-meta-app-closed");
            _system._runtime.FinishApplicationInvocation(_runtime);
            _system._runtime.TryReleaseApplicationMemory(Registration.RuntimeGeneration, _packageLease);
            if (_system._applicationWork.TryGetValue((Registration.Host, Registration.RuntimeGeneration), out var current) && ReferenceEquals(current, this))
            { _system._applicationWork.Remove((Registration.Host, Registration.RuntimeGeneration)); _system._applications.Remove((Registration.Host, Registration.RuntimeGeneration)); }
            _ingress.Clear(); _actions.Clear(); _controls.Clear(); _views.Clear(); _outputs.Clear(); _members.Clear();
        }
    }
}
